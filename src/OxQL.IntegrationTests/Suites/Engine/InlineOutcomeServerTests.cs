using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What an inline resolve detects on a real server (PRE-2, RE-4): onto a member that is not the
/// target's key, a key two records hold is <c>ambiguous</c> and the first by record key is taken,
/// whatever the page holds; with a filter under <c>onMissing</c>, a record the filter excluded is
/// told apart from a missing one in the aggregate, so strict binds and joins what a lenient request
/// does, through a projection in between.
/// </summary>
[Trait("Category", "Integration")]
public class InlineOutcomeServerTests
{
    public const string Invoice = "io.invoice";
    public const string Customer = "io.customer";

    public sealed class InvoiceModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Number { get; set; } = "";

        [OxQLReference(Customer, "code")]
        public string? CustomerCode { get; set; }

        [OxQLReference(Customer)]
        public Guid? CustomerId { get; set; }
    }

    public sealed class CustomerModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public string Code { get; set; } = "";
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build(
    [
        new EntityDeclaration(Invoice, Invoice, typeof(InvoiceModel), "tmp_io_invoices", null, false),
        new EntityDeclaration(Customer, Customer, typeof(CustomerModel), "tmp_io_customers", null, false),
    ]));

    private static BsonBinaryData IdOf(int n) => new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), GuidRepresentation.Standard);

    private static BsonDocument CustomerRow(int n, string name, string code) =>
        new() { ["_id"] = IdOf(n), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Name"] = name, ["Code"] = code };

    private static BsonDocument InvoiceRow(int n, string number, string? code = null, int? customer = null) => new()
    {
        ["_id"] = IdOf(100 + n),
        ["OrganizationId"] = EngineDirect.OrganisationValue,
        ["Number"] = number,
        ["CustomerCode"] = code is null ? BsonNull.Value : new BsonString(code),
        ["CustomerId"] = customer is { } id ? IdOf(id) : BsonNull.Value,
    };

    private static async Task<(OxQL.IntegrationTests.Fleet.TestDatabase Owned, IQueryEngine Engine)> SeedAsync()
    {
        var owned = await OxQL.IntegrationTests.Fleet.MongoFixture.CreateDatabaseAsync("inline_outcome");

        // Two customers share the code ACME; the second one's key is lower, so it is the first by key.
        await owned.Database.GetCollection<BsonDocument>("tmp_io_customers").InsertManyAsync(
        [
            CustomerRow(2, "Acme second", "ACME"),
            CustomerRow(1, "Acme first", "ACME"),
            CustomerRow(3, "Solo", "SOLO"),
        ]);
        await owned.Database.GetCollection<BsonDocument>("tmp_io_invoices").InsertManyAsync(
        [
            InvoiceRow(1, "I-1", code: "ACME", customer: 1),
            InvoiceRow(2, "I-2", code: "SOLO", customer: 3),
            InvoiceRow(3, "I-3", code: "NONE", customer: 99),
        ]);

        return (owned, Direct.Engine(await OxQL.IntegrationTests.Fleet.MongoFixture.ClientAsync(), owned.Name));
    }

    private static async Task<QueryOutcome> RunAsync(IQueryEngine engine, string pipeline, bool strict = false) =>
        await engine.ExecuteAsync(EngineDirect.Request(Invoice, pipeline) with { Strict = strict ? true : null }, Direct.Context());

    [Fact]
    public async Task I01_a_code_two_customers_hold_is_ambiguous_takes_the_first_by_key_and_refuses_under_strict()
    {
        var (owned, engine) = await SeedAsync();
        await using var _ = owned;
        const string Pipeline = """
            [ { "resolve": { "path": "customerCode", "as": "c", "select": ["name"], "onMissing": "report" } },
              { "project": { "number": 1, "c": 1 } },
              { "sort": [ { "number": "asc" } ] } ]
            """;

        var outcome = await RunAsync(engine, Pipeline);
        var result = outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;

        result.Items.Select(item => item!["c"]?["name"]?.GetValue<string>()).Should().Equal("Acme first", "Solo", null);
        result.Items.Should().OnlyContain(item => item!.AsObject().All(member => !member.Key.StartsWith(Aliases.ReservedPrefix, StringComparison.Ordinal)));
        result.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveAmbiguous, Codes.ResolveMissing);

        var rows = (JsonArray)System.Text.Json.JsonSerializer.SerializeToNode(result.Diagnostics![0].Params!["rows"])!;
        rows.Should().ContainSingle().Which!["row"]!.GetValue<int>().Should().Be(0);

        var strict = await RunAsync(engine, Pipeline.Replace("\"report\"", "\"null\""), strict: true);

        strict.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveAmbiguous);
    }

    [Fact]
    public async Task I02_a_filtered_resolve_tells_an_excluded_customer_from_a_missing_one_and_strict_answers_what_lenient_answers()
    {
        var (owned, engine) = await SeedAsync();
        await using var _ = owned;
        const string Pipeline = """
            [ { "resolve": { "path": "customerId", "as": "c", "select": ["name"], "filter": { "name": { "eq": "Acme first" } }ONMISSING } },
              { "project": { "number": 1, "c": 1 } },
              { "sort": [ { "c.name": "asc" }, { "number": "asc" } ] } ]
            """;

        var reported = await RunAsync(engine, Pipeline.Replace("ONMISSING", """, "onMissing": "report" """));
        var result = reported.Should().BeOfType<QueryOutcome.Success>(reported is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;

        result.Items.Select(item => item!["number"]!.GetValue<string>()).Should().Equal("I-2", "I-3", "I-1");
        var missing = result.Diagnostics!.Should().ContainSingle().Subject;
        missing.Code.Should().Be(Codes.ResolveMissing, "I-2's customer exists and the filter excluded it; only I-3's does not exist");
        ((JsonArray)System.Text.Json.JsonSerializer.SerializeToNode(missing.Params!["rows"])!).Select(row => row!["row"]!.GetValue<int>()).Should().Equal(1);

        var lenient = await RunAsync(engine, Pipeline.Replace("ONMISSING", ""));
        var strict = await RunAsync(engine, Pipeline.Replace("ONMISSING", ""), strict: true);

        lenient.Should().BeOfType<QueryOutcome.Success>();
        strict.Should().BeOfType<QueryOutcome.Refused>("strict sorts on the alias as lenient does, and refuses the one missing customer")
            .Which.Refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveMissing);
    }
}
