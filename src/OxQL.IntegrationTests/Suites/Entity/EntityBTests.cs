using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Entity;

/// <summary>
/// Area B: what <c>entityType</c> resolves to. It is the only free-text member a caller controls
/// and everything downstream binds against it: an exact, case-sensitive match; a typo, an empty
/// or a blank id refused; a retired id answered as the current entity with a diagnostic that says
/// so; and an entity resolved only by the host that owns it. Ported from the legacy
/// <c>entity-b</c> battery; the generated-module cases went to the client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class EntityBTests
{
    private const string Page1 = """[ { "page": { "limit": 1 } } ]""";

    private static Task<LabClient> Transport(int? contract = 2) => Lab.ClientAsync(LabService.Transport, contract: contract);

    [Theory]
    [InlineData("Transport.Shipment")]
    [InlineData("TRANSPORT.SHIPMENT")]
    [InlineData("transport.Shipment")]
    public async Task B01_entity_type_is_matched_exactly_and_case_sensitively(string entityType)
    {
        var answer = await (await Transport()).SendAsync(entityType, Page1);

        answer.ShouldRefuse("UNKNOWN_ENTITY", 400, entityType);
        answer.ErrorCodes.Should().Equal("UNKNOWN_ENTITY");
    }

    [Theory]
    [InlineData("transport.shipmnet")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("shipment")]
    public async Task B02_B03_a_typo_an_empty_id_a_blank_id_and_a_bare_name_are_unknown_entities(string entityType)
    {
        var answer = await (await Transport()).SendAsync(entityType, Page1);

        answer.ShouldRefuse("UNKNOWN_ENTITY", 400, $"'{entityType}'");
        answer.ErrorCodes.Should().Equal("UNKNOWN_ENTITY");
    }

    [Theory]
    [InlineData("fleet", "department", Corpus.Department)]
    [InlineData("fleet", "equipment", Corpus.Equipment)]
    [InlineData("ledger", "transaction", Corpus.Transaction)]
    public async Task B04_B05_a_retired_id_answers_the_current_entitys_rows_with_a_diagnostic_naming_the_live_id(string serviceKey, string retiredId, string currentId)
    {
        var service = LabService.Of(serviceKey);
        var expected = Corpus.AllIds(currentId);
        expected.Should().NotBeEmpty();
        const string pipeline = """[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } } ]""";

        var viaRetired = await RetiredIdHost.SendAsync(service, retiredId, pipeline);

        // B4: the same rows, not merely a 200.
        viaRetired.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);

        // B5: and a diagnostic that names where they came from.
        viaRetired.DiagnosticCodes.Should().Equal("ENTITY_ID_RETIRED");
        viaRetired.Diagnostics[0]["params"]!["currentId"]!.GetValue<string>().Should().Be(currentId);

        // The current id carries no diagnostic on the same host, so the diagnostic is the
        // retirement and not noise.
        var viaCurrent = await RetiredIdHost.SendAsync(service, currentId, pipeline);
        viaCurrent.ShouldHaveIds(expected).ShouldHaveNoDiagnostics();
        viaCurrent.Body!.AsObject().ContainsKey("diagnostics").Should().BeFalse();

        // Contract 1 matches a retired id case-insensitively, as it does every id.
        var legacy = await RetiredIdHost.SendAsync(service, retiredId.ToUpperInvariant(), pipeline, contract: null);
        legacy.ShouldBeOk();
        legacy.TotalCount.Should().Be(expected.Count);
        legacy.DiagnosticCodes.Should().Equal("ENTITY_ID_RETIRED");
    }

    [Fact]
    public async Task B04b_a_retired_id_is_unknown_on_a_host_that_does_not_declare_it()
    {
        // The standard fleet host declares no retired ids: the id is not an alias there.
        var answer = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync("department", Page1);

        answer.ShouldRefuse("UNKNOWN_ENTITY", 400);
    }

    [Theory]
    [InlineData("fleet", "$Department")]
    [InlineData("transport", "$Shipment")]
    [InlineData("staff", "$Employee")]
    public async Task B06_a_legacy_class_name_id_is_not_an_entity_id_under_either_contract(string serviceKey, string legacyId)
    {
        foreach (var contract in new int?[] { 2, null })
        {
            var answer = await (await Lab.ClientAsync(LabService.Of(serviceKey), contract: contract)).SendAsync(legacyId, Page1);

            answer.ShouldRefuse("UNKNOWN_ENTITY", 400, $"{legacyId} under contract {contract?.ToString() ?? "1 (no header)"}");
            answer.ErrorCodes.Should().Equal("UNKNOWN_ENTITY");
        }
    }

    public static TheoryData<string> Entities() => new(Corpus.Entities.Select(entity => entity.Id));

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task B07_every_lab_entity_answers_a_bare_first_page_with_exactly_the_corpus_count(string entityId)
    {
        var entity = Corpus.Entity(entityId);
        var all = Corpus.Rows(entityId).Select(row => row.WireId).ToList();
        all.Count.Should().BeGreaterThan(1);
        var key = entity.KeyPath;

        var answer = await (await Lab.ClientForAsync(entityId)).SendAsync(entityId, $$"""[ { "sort": [ { "{{key}}": "asc" } ] }, { "page": { "limit": 3, "includeTotalCount": true } } ]""");

        // Organisation B clones A on the filtered members, so a wrong count here is a scope leak.
        answer.ShouldHaveTotal(all.Count);
        answer.Strings(key).Should().Equal(Corpus.Rows(entityId).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Take(3).Select(row => row.WireId));
    }

    [Fact]
    public async Task B08_an_entity_is_resolved_by_the_host_that_owns_it_never_by_a_sibling_host()
    {
        var answer = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync(Corpus.Shipment, Page1);

        answer.ShouldRefuse("UNKNOWN_ENTITY", 400);
        answer.ErrorCodes.Should().Equal("UNKNOWN_ENTITY");
    }

    [Fact]
    public async Task B12_a_path_no_client_could_check_is_refused_by_the_service_never_an_empty_page()
    {
        // The dynamic query's client half (the client abstains without metadata) went to the
        // client's specs; the engine half is that the service refuses what reaches it.
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[ { "match": { "nope": { "eq": 1 } } }, { "page": { "limit": 1 } } ]""");

        answer.ShouldRefuse("UNKNOWN_PATH", 400);
    }
}
