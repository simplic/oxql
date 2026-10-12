using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
using OxQL.Model;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// Explain ≡ execution for a condition on a member of another service's rows (a semi-join), over the
/// fleet's real hosts and their database: what explain says of the condition is what running the
/// request does. The owner binds the condition when the query runs, before the page; explain has the
/// owner bind the query the run sends first, in the call the owner is asked in anyway.
/// <list type="bullet">
///   <item>The lab's case: <c>recipientContact.id contains "x"</c> on A3 is 422 <c>RESOLVE_REFUSED</c> /
///   <c>INVALID_OPERAND</c> when run, and <c>INVALID_OPERAND</c> at the stage (or the filter entry) in
///   explain, <c>executable: "never"</c>.</item>
///   <item>The parity theory: for every reference query, every column of another service's rows (and one
///   further member of every kind the owner's model has under each alias that takes a condition), every
///   operator, with an operand of the member's kind and with one that is not: a run that is refused for
///   how the request binds is a request explain refuses with the run's codes, and a run that answers is a
///   request explain calls valid. And <c>can.filter</c> is exactly the operators a run accepts.</item>
///   <item>The cost: the condition rides in the owner's one call of the round; the cached tier asks nobody
///   and says the condition is unchecked.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class ExplainConditionFleetTests(ITestOutputHelper output)
{
    private static readonly string[] Operators = ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex", "is", "any"];

    private static readonly IReadOnlyDictionary<string, string?> Unlimited = new Dictionary<string, string?>();

    /// <summary>
    /// Whether a run was refused for how the request binds: 400 at this host, or 422 with the errors an
    /// owner's binder gave (<c>RESOLVE_REFUSED</c> wrapping them), or no way to an owner at all.
    /// </summary>
    private static bool BindRefused(WireAnswer run) =>
        run.Status == HttpStatusCode.BadRequest
        || (run.StatusCode == 422 && (run.ErrorCodes.FirstOrDefault() == "RESOLVE_UNAVAILABLE" || (run.ErrorCodes.FirstOrDefault() == "RESOLVE_REFUSED" && run.ErrorCodes.Count > 1)));

    /// <summary>
    /// Whether a run was refused for what the owner's rows hold, which only a run can tell: 422
    /// <c>RESOLVE_REFUSED</c> wrapping no owner error. The owner bound the condition and answered rows;
    /// one of them lacks the member the reference names (a reference onto a member that is not the
    /// key, here an employee without a user), and the semi-join refuses rather than read the row as
    /// "no such record". Explain binds and never reads a row.
    /// </summary>
    private static bool DataRefused(WireAnswer run) =>
        run.StatusCode == 422 && run.ErrorCodes.SequenceEqual(["RESOLVE_REFUSED"]) && run.Errors[0]["message"]!.GetValue<string>().Contains("answered a row without the projected member", StringComparison.Ordinal);

    /// <summary>The codes a run was refused with for how it binds: its errors, without the head that wraps an owner's.</summary>
    private static List<string> RefusedWith(WireAnswer run) =>
        run.StatusCode == 422 ? run.ErrorCodes.Skip(1).ToList() : run.ErrorCodes.ToList();

    private static JsonObject WithStage(JsonObject request, int at, string stage)
    {
        var changed = request.DeepClone().AsObject();

        changed["pipeline"]!.AsArray().Insert(at, JsonNode.Parse(stage));

        return changed;
    }

    private static JsonObject WithFilter(JsonObject request, string filter)
    {
        var changed = request.DeepClone().AsObject();

        changed["filter"] = JsonNode.Parse(filter);

        return changed;
    }

    /// <summary>Where a grid writes a condition: before the page, or at the end of a request without one.</summary>
    private static int BeforePage(JsonObject request)
    {
        var pipeline = request["pipeline"]!.AsArray();

        return pipeline.Count > 0 && pipeline[^1]!.AsObject().ContainsKey("page") ? pipeline.Count - 1 : pipeline.Count;
    }

    /// <summary>
    /// What stands between the errors a run was refused with for how it binds and the errors explain
    /// answers: null when they are the same errors (code, message, stage or filter entry, path, params;
    /// explain's carry a basis on top, the run's 422 a head that wraps them).
    /// </summary>
    private static string? SameErrors(WireAnswer run, WireAnswer explained, bool among = false)
    {
        var ran = (run.StatusCode == 422 ? run.Errors.Skip(1) : run.Errors).Select(error => error.ToJsonString()).ToList();
        var said = explained.Body!["errors"]!.AsArray().Select(error =>
        {
            var copy = error!.DeepClone().AsObject();

            copy.Remove("basis");

            return copy.ToJsonString();
        }).ToList();

        // A run stops at the first owner that refuses; explain asks every target's owner and says an
        // error once per code, stage and path, in the words of the first target that gave it. Over
        // several targets the run's errors are therefore among explain's by code, stage and path.
        if (among)
        {
            static string Key(JsonNode? error) => $"{error!["code"]}|{error["stage"]}|{error["filter"]}|{error["path"]}";

            var known = explained.Body["errors"]!.AsArray().Select(Key).ToHashSet(StringComparer.Ordinal);
            var met = (run.StatusCode == 422 ? run.Errors.Skip(1) : run.Errors).Select(error => Key(error)).ToList();

            if (met.Count > 0 && met.All(known.Contains))
                return null;
        }

        return ran.SequenceEqual(said) ? null : $"the run's errors are {string.Join(" ", ran)}, explain's {string.Join(" ", said)}";
    }

    private static string Said(WireAnswer explained) =>
        string.Join("; ", explained.Body!["errors"]!.AsArray().Select(error => $"{error!["code"]}@{error["stage"] ?? error["filter"]} {error["path"]}"));

    // ---- the lab's case -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "contains": "x" }""")]
    [InlineData("""{ "eq": "x" }""")]
    [InlineData("""{ "gt": "7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11" }""")]
    public async Task L1_a_condition_the_owner_refuses_for_a_guid_member_is_refused_by_explain_as_by_the_run(string condition)
    {
        var request = ReportScenarios.Request("A3");
        var resolve = ReportScenarios.IndexOf(request, "recipientContact");
        var written = WithStage(request, resolve + 1, $$"""{ "match": { "recipientContact.id": {{condition}} } }""");
        var client = await ReportScenarios.ClientAsync(written);

        var run = await client.QueryAsync(written);
        var explained = await client.ExplainHereAsync(written);

        run.StatusCode.Should().Be(422, run.ToString());
        run.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "INVALID_OPERAND");
        run.Errors[0]["stage"]!.GetValue<int>().Should().Be(resolve + 1, "the head lies where the owner's error lies");
        SameErrors(run, await client.ExplainHereAsync(written)).Should().BeNull("a run's refusal carries what explain's error carries");

        explained.Status.Should().Be(HttpStatusCode.OK, explained.Text);
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse("explain says what the run does");
        explained.Body["executable"]!.GetValue<string>().Should().Be("never");

        var error = explained.Body["errors"]!.AsArray().Should().ContainSingle().Subject!.AsObject();

        output.WriteLine($"run: {run.Text}");
        output.WriteLine($"explain: {error.ToJsonString()}");
        error["code"]!.GetValue<string>().Should().Be("INVALID_OPERAND");
        error["stage"]!.GetValue<int>().Should().Be(resolve + 1);
        error["path"]!.GetValue<string>().Should().Be("recipientContact.id");
        error["basis"]!.GetValue<string>().Should().Be("model");
        error["message"]!.GetValue<string>().Should().Be(run.Errors[1]["message"]!.GetValue<string>(), "the owner's own words, as the run passes them on");
        error["params"]!["owner"]!.ToJsonString().Should().Be("""{"service":"directory","entity":"directory.contact","target":"directory.contact","stage":0,"path":"id"}""");
        explained.Body["stages"]!.AsArray().Select(stage => stage!["status"]!.GetValue<string>()).Should().Equal(
            Enumerable.Range(0, written["pipeline"]!.AsArray().Count).Select(index => index == resolve + 1 ? "error" : "ok"));
        explained.Body["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task L2_the_same_condition_in_the_requests_filter_is_refused_at_its_entry_by_id()
    {
        var request = ReportScenarios.Request("A3");
        var written = WithFilter(request, """[ { "id": "recipient", "where": { "recipientContact.id": { "contains": "x" } } }, { "id": "company", "where": { "recipientContact.address.companyName": { "contains": "x" } } } ]""");
        var client = await ReportScenarios.ClientAsync(written);

        var run = await client.QueryAsync(written);
        var explained = await client.ExplainHereAsync(written);

        run.StatusCode.Should().Be(422, run.ToString());
        run.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "INVALID_OPERAND");
        output.WriteLine($"run: {run.Text}");
        run.Errors[0]["filter"]!.GetValue<string>().Should().Be("recipient", "the head names the entry");
        run.Errors[0].ContainsKey("stage").Should().BeFalse();
        SameErrors(run, explained).Should().BeNull("a run's refusal carries what explain's error carries");
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body["executable"]!.GetValue<string>().Should().Be("never");

        var error = explained.Body["errors"]!.AsArray().Should().ContainSingle().Subject!.AsObject();

        output.WriteLine($"explain: {error.ToJsonString()} filter: {explained.Body["filter"]!.ToJsonString()}");
        error["code"]!.GetValue<string>().Should().Be("INVALID_OPERAND");
        error["filter"]!.GetValue<string>().Should().Be("recipient", "the entry's id is echoed");
        error.ContainsKey("stage").Should().BeFalse("an error of a filter entry carries no stage");
        error["path"]!.GetValue<string>().Should().Be("recipientContact.id");
        error["basis"]!.GetValue<string>().Should().Be("model");
        explained.Body["filter"]!.AsArray().Select(entry => $"{entry!["id"]}:{entry["status"]}").Should().Equal("recipient:error", "company:ok");
        explained.Body["stages"]!.AsArray().Should().OnlyContain(stage => stage!["status"]!.GetValue<string>() == "ok");

        // Without the refused entry the request runs, and explain says so.
        var accepted = WithFilter(request, """[ { "id": "company", "where": { "recipientContact.address.companyName": { "contains": "x" } } } ]""");
        var ran = await client.QueryAsync(accepted);
        var said = await client.ExplainHereAsync(accepted);

        BindRefused(ran).Should().BeFalse(ran.ToString());
        said.Body!["valid"]!.GetValue<bool>().Should().BeTrue(said.Text);
        said.Body["executable"]!.GetValue<string>().Should().Be("yes");
    }

    [Fact]
    public async Task L4_a_condition_under_the_owners_addon_bag_is_the_organisations_to_change_and_the_verdict_unknown()
    {
        var request = ReportScenarios.Request("A3");
        var resolve = ReportScenarios.IndexOf(request, "recipientContact");
        var written = WithStage(request, resolve + 1, """{ "match": { "recipientContact.addon.noSuchKeyOfEng5": { "gt": 1 } } }""");
        var client = await ReportScenarios.ClientAsync(written);

        var run = await client.QueryAsync(written);
        var explained = await client.ExplainHereAsync(written);

        run.StatusCode.Should().Be(422, run.ToString());
        run.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "NOT_FILTERABLE");
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);

        var error = explained.Body["errors"]!.AsArray().Should().ContainSingle().Subject!.AsObject();

        output.WriteLine($"explain: {error.ToJsonString()}");
        error["code"]!.GetValue<string>().Should().Be("NOT_FILTERABLE");
        error["path"]!.GetValue<string>().Should().Be("recipientContact.addon.noSuchKeyOfEng5");
        error["basis"]!.GetValue<string>().Should().Be("organisation", "the owner said whose it is to change");
        error["params"]!["addon"]!.ToJsonString().Should().Be("""{"key":"noSuchKeyOfEng5","state":"undefined"}""");
        error["params"]!["owner"]!["service"]!.GetValue<string>().Should().Be("directory");
        explained.Body["executable"]!.GetValue<string>().Should().Be("unknown", "a definition of the organisation changes it, no deployment");
    }

    [Fact]
    public async Task L5_a_remote_resolves_own_filter_the_owner_refuses_is_refused_by_explain_at_the_resolve_as_by_the_run()
    {
        var request = ReportScenarios.Request("A3");
        var resolve = ReportScenarios.IndexOf(request, "recipientContact");

        request["pipeline"]![resolve]!["resolve"]!["filter"] = JsonNode.Parse("""{ "id": { "contains": "x" } }""");

        var client = await ReportScenarios.ClientAsync(request);
        var run = await client.QueryAsync(request);
        var explained = await client.ExplainHereAsync(request);

        output.WriteLine($"run: {run.Text}");
        run.StatusCode.Should().Be(422, run.ToString());
        run.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "INVALID_OPERAND");
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body["executable"]!.GetValue<string>().Should().Be("never");

        var error = explained.Body["errors"]!.AsArray().Should().ContainSingle().Subject!.AsObject();

        output.WriteLine($"explain: {error.ToJsonString()}");
        error["code"]!.GetValue<string>().Should().Be("INVALID_OPERAND");
        error["stage"]!.GetValue<int>().Should().Be(resolve);
        error["path"]!.GetValue<string>().Should().Be("id", "the path as the caller wrote it in the stage's filter");
        error["basis"]!.GetValue<string>().Should().Be("model");
        error["params"]!["owner"]!.ToJsonString().Should().Be("""{"service":"directory","entity":"directory.contact","target":"directory.contact","stage":1,"path":"id"}""");
        explained.Body["stages"]![resolve]!["status"]!.GetValue<string>().Should().Be("error");
        SameErrors(run, explained).Should().BeNull("a run's refusal carries what explain's error carries");

        // A filter the owner binds leaves both as they were.
        request["pipeline"]![resolve]!["resolve"]!["filter"] = JsonNode.Parse("""{ "address.companyName": { "contains": "x" } }""");

        var ran = await client.QueryAsync(request);
        var said = await client.ExplainHereAsync(request);

        said.Body!["valid"]!.GetValue<bool>().Should().BeTrue(said.Text);
        said.Body["executable"]!.GetValue<string>().Should().Be("yes");
        BindRefused(ran).Should().BeFalse(ran.ToString());
    }

    [Fact]
    public async Task L6_a_condition_through_a_reference_onto_a_member_that_is_not_the_key_is_noted_as_the_runs_to_tell()
    {
        // The clerk is referenced by its user id, which is not the employee's key.
        var byMember = ReportScenarios.Request("A4");
        var explained = await (await ReportScenarios.ClientAsync(byMember)).ExplainHereAsync(
            WithStage(byMember, ReportScenarios.IndexOf(byMember, "clerk") + 1, """{ "match": { "clerk.address.lastName": { "startsWith": "M" } } }"""));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body["executable"]!.GetValue<string>().Should().Be("yes", "a note about what only a run can tell changes no verdict");

        var note = explained.Body["notes"]!.AsArray().Should().ContainSingle(note => note!["code"]!.GetValue<string>() == "SEMI_JOIN_NOT_BY_KEY").Subject!.AsObject();

        output.WriteLine($"explain: {note.ToJsonString()}");
        note["basis"]!.GetValue<string>().Should().Be("data");
        note["stage"]!.GetValue<int>().Should().Be(ReportScenarios.IndexOf(byMember, "clerk") + 1);
        note["path"]!.GetValue<string>().Should().Be("clerk.address.lastName");
        note["params"]!.ToJsonString().Should().Be("""{"alias":"clerk","target":"staff.employee","field":"userId","services":["staff"]}""");

        // The recipient is referenced by its key: no such note.
        var byKey = ReportScenarios.Request("A3");
        var plain = await (await ReportScenarios.ClientAsync(byKey)).ExplainHereAsync(
            WithStage(byKey, ReportScenarios.IndexOf(byKey, "recipientContact") + 1, """{ "match": { "recipientContact.address.companyName": { "contains": "x" } } }"""));

        plain.Body!["notes"]!.AsArray().Should().NotContain(note => note!["code"]!.GetValue<string>() == "SEMI_JOIN_NOT_BY_KEY");
    }

    // ---- the cost -----------------------------------------------------------------------------------------

    [Fact]
    public async Task L3_the_condition_rides_in_the_owners_one_call_and_the_cached_tier_asks_nobody_and_says_unchecked()
    {
        var fleet = await CorpusFleet.SharedAsync();
        var origin = fleet.Variant(LabService.Ledger, "eng5-condition", Unlimited, Org.R).AsUser(Guid.NewGuid());
        var remote = (InMemoryRemoteClient)(await origin.HostAsync()).Services.GetRequiredService<IRemoteQueryClient>();
        var request = ReportScenarios.Request("A3");
        var resolve = ReportScenarios.IndexOf(request, "recipientContact");

        JsonObject Asking(string operand, string tier) => new()
        {
            ["query"] = WithStage(request, resolve + 1, $$"""{ "match": { "recipientContact.address.companyName": { "contains": "{{operand}}" } } }"""),
            ["remote"] = tier,
        };

        // Nothing kept: the cached tier asks no owner, and neither part is called valid by silence.
        var cold = await origin.ExplainHereAsync(Asking("a", "cached"));

        remote.ExplainCalls.Should().BeEmpty();
        cold.Body!["valid"]!.GetValue<bool>().Should().BeTrue(cold.Text);
        cold.Body["executable"]!.GetValue<string>().Should().Be("unknown");
        output.WriteLine($"cached, nothing kept: {string.Join(" ", cold.Body["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "REMOTE_UNCHECKED").Select(note => note!.ToJsonString()))}");
        cold.Body["notes"]!.AsArray().Should().Contain(note => note!["code"]!.GetValue<string>() == "REMOTE_UNCHECKED" && note["path"] != null
            && note["path"]!.GetValue<string>() == "recipientContact.address.companyName" && note["stage"]!.GetValue<int>() == resolve + 1
            && note["params"]!["reason"]!.GetValue<string>() == "cached" && note["basis"]!.GetValue<string>() == "owner");

        // A check: one call to the owner, carrying the target's check and the condition's.
        var check = await origin.ExplainHereAsync(Asking("a", "check"));
        var calls = remote.ExplainCalls.ToList();

        check.Body!["executable"]!.GetValue<string>().Should().Be("yes", check.Text);
        calls.Should().ContainSingle().Which.Service.Should().Be("directory");
        calls[0].Batch.Checks.Should().HaveCount(2, "the remote target's query and the condition's, in one call");
        check.Body["owners"]!.AsArray().Single(owner => owner!["service"]!.GetValue<string>() == "directory")!["calls"]!.GetValue<int>().Should().Be(1);
        output.WriteLine($"check, cold: 1 owner call, {calls[0].Batch.Checks.Count} checks, {calls[0].Bytes} B answered");

        var condition = calls[0].Batch.Checks.Single(sent => sent.Query.Pipeline[0].Match?.Condition?.Path == "address.companyName");

        condition.Include.Should().Equal(ExplainRequest.IncludeNotes);
        condition.Slim.Should().BeTrue();

        // Warm, and the cached tier after it: nobody is asked, and the answer is the check's.
        var warm = await origin.ExplainHereAsync(Asking("a", "check"));
        var cached = await origin.ExplainHereAsync(Asking("a", "cached"));

        remote.ExplainCalls.Should().HaveCount(1);
        cached.Body!["executable"]!.GetValue<string>().Should().Be("yes");
        cached.Body["etag"]!.GetValue<string>().Should().Be(check.Body["etag"]!.GetValue<string>());
        warm.Body!["etag"]!.GetValue<string>().Should().Be(check.Body["etag"]!.GetValue<string>());

        // Another operand is another owner query: the cached tier does not know it; a check asks the
        // owner once, for the condition alone (the target's answer is kept).
        var typed = await origin.ExplainHereAsync(Asking("ab", "cached"));

        remote.ExplainCalls.Should().HaveCount(1);
        typed.Body!["executable"]!.GetValue<string>().Should().Be("unknown");
        typed.Body["cache"]!["complete"]!.GetValue<bool>().Should().BeFalse();

        var settled = await origin.ExplainHereAsync(Asking("ab", "check"));

        remote.ExplainCalls.Should().HaveCount(2);
        remote.ExplainCalls.Last().Batch.Checks.Should().ContainSingle("only the condition is new");
        settled.Body!["executable"]!.GetValue<string>().Should().Be("yes");
        output.WriteLine($"check, the target kept: 1 owner call, 1 check, {remote.ExplainCalls.Last().Bytes} B answered");
    }

    // ---- the parity theory --------------------------------------------------------------------------------

    public static TheoryData<string> Reference => new(ExplainGolden.Cases.Keys);

    private static readonly Lazy<IReadOnlyDictionary<string, EntityDef>> Entities = new(() =>
        LabService.All.SelectMany(service => service.Model.Entities.Values).DistinctBy(entity => entity.Id).ToDictionary(entity => entity.Id, StringComparer.Ordinal));

    /// <summary>A value of a kind, as a condition's operand; null where this suite has none to offer.</summary>
    private static string? ValueOf(PathDef path) => path.LeafKind switch
    {
        Kind.Guid => "\"7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11\"",
        Kind.String => "\"x\"",
        Kind.Int or Kind.Long => "1",
        Kind.Double or Kind.Decimal => "1.5",
        Kind.Bool => "true",
        Kind.Date => "\"2024-01-01\"",
        Kind.DateTime => "\"2024-01-01T00:00:00Z\"",
        Kind.Enum when path.Shape.Leaf.Type?.EnumValues.FirstOrDefault(value => value.Active) is { } member => $"\"{member.Name}\"",
        _ => null,
    };

    /// <summary>The operand of an operator around a value: a list for a set, a flag for <c>exists</c>, a pattern, a variant, an element condition.</summary>
    private static string Operand(string op, string value) => op switch
    {
        "in" or "nin" => $"[{value}]",
        "exists" => "true",
        "contains" or "startsWith" or "endsWith" => "\"x\"",
        "regex" => "\"^x\"",
        "is" => "\"X\"",
        "any" => """{ "id": { "eq": 1 } }""",
        _ => value,
    };

    /// <summary>
    /// For every reference query: every column of another service's rows, and under every alias that
    /// takes a condition one further member of each kind its owner's model has, with every operator.
    /// The condition is written where a grid writes it, before the page.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reference))]
    public async Task P1_every_operator_on_every_remote_column_is_accepted_by_a_run_or_refused_by_explain_with_the_runs_codes(string id)
    {
        var request = ExplainGolden.Cases[id]();
        var client = await ReportScenarios.ClientAsync(request);
        var answer = await client.ExplainHereAsync(request);

        // A reference query an owner refuses has no final row to ask about.
        if (answer.Body?["valid"]?.GetValue<bool>() != true)
            return;

        var result = answer.Body["result"]!;
        var capabilities = result["capabilities"]!.AsObject();
        var at = BeforePage(request);
        var disagreements = new List<string>();
        var compared = 0;
        var byData = 0;
        var refused = 0;
        var accepted = 0;
        var columns = 0;

        // The entity whose rows an alias holds: the one target of a plain remote resolve.
        string? OwnerOf(string root) => result["subjects"]!.AsArray().Select(subject => subject!.AsObject())
            .FirstOrDefault(subject => subject["root"]!.GetValue<string>() == root && subject["remote"]?.GetValue<bool>() == true)?["entity"]?.GetValue<string>();

        async Task<bool?> CompareAsync(string path, string op, string operand)
        {
            var written = WithStage(request, at, $$"""{ "match": { "{{path}}": { "{{op}}": {{operand}} } } }""");
            var run = await client.QueryAsync(written);
            var explained = await client.ExplainHereAsync(written);

            compared++;

            if (explained.StatusCode != 200)
            {
                disagreements.Add($"{id}: '{path}' {op} {operand}: explain answered {explained.StatusCode}");
                return null;
            }

            var valid = explained.Body!["valid"]!.GetValue<bool>();
            var bindRefused = BindRefused(run);

            if (valid == bindRefused)
            {
                disagreements.Add($"{id}: '{path}' {op} {operand}: explain {(valid ? "is valid" : $"refuses ({Said(explained)})")}, the run answered {run.StatusCode} {string.Join(",", run.ErrorCodes)}: {string.Join(" | ", run.Errors.Select(error => error["message"]?.GetValue<string>()))}");
                return null;
            }

            if (!bindRefused)
            {
                accepted++;

                if (DataRefused(run))
                    byData++;
                else if (run.StatusCode != 200)
                    disagreements.Add($"{id}: '{path}' {op} {operand}: explain is valid, the run answered {run.StatusCode} {string.Join(",", run.ErrorCodes)}: {string.Join(" | ", run.Errors.Select(error => error["message"]?.GetValue<string>()))}");

                if (explained.Body["executable"]!.GetValue<string>() != "yes")
                    disagreements.Add($"{id}: '{path}' {op} {operand}: the run answered {run.StatusCode}, explain's verdict is '{explained.Body["executable"]}'");

                return true;
            }

            refused++;

            var codes = explained.Body["errors"]!.AsArray().Select(error => error!["code"]!.GetValue<string>()).ToList();

            if (!codes.SequenceEqual(RefusedWith(run)))
                disagreements.Add($"{id}: '{path}' {op} {operand}: explain refuses with {string.Join(",", codes)}, the run with {string.Join(",", RefusedWith(run))}");
            else if (SameErrors(run, explained) is { } differing)
                disagreements.Add($"{id}: '{path}' {op} {operand}: {differing}");

            // An owner's refusal is said where the caller wrote the condition, under the caller's path.
            if (run.StatusCode == 422 && !explained.Body["errors"]!.AsArray().All(error => error!["stage"]?.GetValue<int>() == at
                && error["params"]?["owner"] is JsonObject && (error["path"]?.GetValue<string>() ?? "").StartsWith(path.Split('.')[0] + ".", StringComparison.Ordinal)))
                disagreements.Add($"{id}: '{path}' {op} {operand}: an owner's refusal is not said at stage {at} under the alias: {explained.Body["errors"]!.ToJsonString()}");

            return false;
        }

        async Task WalkAsync(string path, PathDef? member, IReadOnlyList<string>? can)
        {
            columns++;

            foreach (var op in Operators)
            {
                // An operand of the member's kind: the operator is what decides.
                var takes = member is null || ValueOf(member) is not { } value ? null : await CompareAsync(path, op, Operand(op, value));

                if (takes is { } taken && can is not null && taken != can.Contains(op))
                    disagreements.Add($"{id}: '{path}' {op}: can.filter {(can.Contains(op) ? "offers" : "does not offer")} it, a run with an operand of its kind {(taken ? "accepts" : "refuses")} it");

                // And one that is not: explain and the run agree on what the owner makes of it.
                await CompareAsync(path, op, Operand(op, member?.LeafKind == Kind.String ? "1" : "\"x\""));
            }
        }

        var walked = new HashSet<string>(StringComparer.Ordinal);
        var semiJoinable = new Dictionary<string, EntityDef>(StringComparer.Ordinal);

        foreach (var column in result["columns"]!.AsArray().Select(column => column!.AsObject()))
        {
            var capability = capabilities[column["can"]!.GetValue<string>()]!;
            var why = capability["why"];

            if (why?["sort"]?.GetValue<string>() != "owner")
                continue;

            var path = column["path"]!.GetValue<string>();
            var root = column["root"]!.GetValue<string>();
            var can = capability["can"]!["filter"]!.AsArray().Select(op => op!.GetValue<string>()).ToList();

            // An alias joined after the page takes no condition: this host refuses it, at explain and at the run alike.
            if (can.Count == 0)
            {
                columns++;
                await CompareAsync(path, "eq", "\"x\"");
                continue;
            }

            why!["filter"]!.GetValue<string>().Should().Be("semiJoin", $"{id}: every owner answered, so '{path}' admits what its kind does");

            var entity = OwnerOf(root) is { } owner && Entities.Value.TryGetValue(owner, out var known) ? known : null;

            entity.Should().NotBeNull($"{id}: '{root}' holds the rows of one entity of the fleet");
            semiJoinable[root] = entity!;
            walked.Add(path);

            await WalkAsync(path, entity!.Paths.FirstOrDefault(member => member.Wire == path[(root.Length + 1)..]), can);
        }

        // The members the row does not show: one of each kind the owner's model has, a member that is
        // none of the owner's, and what is not a scalar.
        foreach (var (root, entity) in semiJoinable)
        {
            foreach (var member in entity.Paths
                .Where(member => member.Stored && !member.Wire.Contains('*', StringComparison.Ordinal) && member.CollectionAncestors == 0 && !walked.Contains(root + "." + member.Wire))
                .GroupBy(member => member.LeafKind == member.Kind ? member.Kind.ToString() : $"{member.Kind} of {member.LeafKind}")
                .Select(group => group.First()))
                await WalkAsync(root + "." + member.Wire, member, null);

            await WalkAsync(root + ".noSuchMember", null, null);
        }

        output.WriteLine($"{id}: {columns} columns and members of other services' rows, {compared} requests explained and run: {accepted} bound by the run ({byData} of them refused for what an owner's row holds, which only a run can tell), {refused} refused for how they bind; {semiJoinable.Count} aliases take a condition ({string.Join(", ", semiJoinable.Select(pair => $"{pair.Key} = {pair.Value.Id}"))})");

        disagreements.Should().BeEmpty();

        if (semiJoinable.Count > 0)
        {
            accepted.Should().BePositive("an operator of the member's kind with an operand of its kind runs");
            refused.Should().BePositive("and one that is not is refused, by explain as by the run");
        }
    }

    /// <summary>A request explain calls not valid only for what an owner refuses, while its run answers.</summary>
    private sealed record Unreached(string What, JsonObject Request, WireAnswer Explained, WireAnswer Run);

    /// <summary>
    /// The same parity for the <c>filter</c> a join stage carries itself, where another service holds the
    /// joined rows: a remote <c>resolve</c> (plain, typed, of an item, continued), a remote <c>lookup</c>
    /// and a union join. For every such stage of every reference query, a filter over one member of each
    /// kind its first target's model has (and one that is no member), with every operator and an operand
    /// of the member's kind, and <c>eq</c> with one that is not. The owner binds the filter when the page
    /// reaches it: a run refused for how it binds is a request explain refuses with the same errors at
    /// that stage (over several targets the run's errors are among explain's: a run stops at the first
    /// owner that refuses, explain asks them all). A request explain refuses only for what an owner refuses may still run when its page
    /// holds no row for that owner; those are counted and bounded, as the route differential bounds them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reference))]
    public async Task P2_every_operator_in_the_filter_of_every_remote_join_is_accepted_by_a_run_or_refused_by_explain_with_the_runs_errors(string id)
    {
        var request = ExplainGolden.Cases[id]();
        var client = await ReportScenarios.ClientAsync(request);
        var answer = await client.ExplainHereAsync(request);

        if (answer.Body?["valid"]?.GetValue<bool>() != true)
            return;

        var disagreements = new List<string>();
        var unreached = new List<Unreached>();
        var compared = 0;
        var refused = 0;
        var accepted = 0;
        var stages = new List<string>();

        async Task CompareAsync(int stage, string kind, string member, string op, string operand, bool several)
        {
            var written = request.DeepClone().AsObject();

            written["pipeline"]![stage]![kind]!["filter"] = JsonNode.Parse($$"""{ "{{member}}": { "{{op}}": {{operand}} } }""");

            var run = await client.QueryAsync(written);
            var explained = await client.ExplainHereAsync(written);
            var what = $"{id}: stage {stage} {kind} filter '{member}' {op} {operand}";

            compared++;

            if (explained.StatusCode != 200)
            {
                if (run.StatusCode != explained.StatusCode)
                    disagreements.Add($"{what}: explain answered {explained.StatusCode}, the run {run.StatusCode}");

                return;
            }

            var valid = explained.Body!["valid"]!.GetValue<bool>();
            var bindRefused = BindRefused(run);

            if (valid && bindRefused)
            {
                disagreements.Add($"{what}: explain is valid, the run answered {run.StatusCode} {string.Join(",", run.ErrorCodes)}: {string.Join(" | ", run.Errors.Select(error => error["message"]?.GetValue<string>()))}");
                return;
            }

            if (!valid && !bindRefused)
            {
                // What only an owner refuses, a page that asks that owner nothing does not meet.
                if (explained.Body["errors"]!.AsArray().All(error => error!["params"]?["owner"] is JsonObject))
                    unreached.Add(new Unreached(what, written, explained, run));
                else
                    disagreements.Add($"{what}: explain refuses ({Said(explained)}), the run answered {run.StatusCode}");

                return;
            }

            if (valid)
            {
                accepted++;
                return;
            }

            refused++;

            if (SameErrors(run, explained, among: several) is { } differing)
                disagreements.Add($"{what}: {differing}");
            else if (run.StatusCode == 422 && !explained.Body["errors"]!.AsArray().All(error => error!["stage"]?.GetValue<int>() == stage && error["params"]?["owner"] is JsonObject))
                disagreements.Add($"{what}: an owner's refusal is not said at the stage: {explained.Body["errors"]!.ToJsonString()}");
        }

        var pipeline = request["pipeline"]!.AsArray();

        for (var stage = 0; stage < pipeline.Count; stage++)
        {
            var written = pipeline[stage]!.AsObject();
            var kind = written.ContainsKey("resolve") ? "resolve" : written.ContainsKey("lookup") ? "lookup" : null;

            if (kind is null || written[kind]!["as"]?.GetValue<string>() is not { } alias || answer.Body["aliases"]![alias] is not JsonObject described
                || described["node"]?.GetValue<string>() != "remote" || described["entities"] is not JsonArray { Count: > 0 } entities)
                continue;

            // The first target's members: of the entity, or of the element of an item target.
            var target = entities[0]!.GetValue<string>();
            var hash = target.IndexOf('#', StringComparison.Ordinal);

            if (!Entities.Value.TryGetValue(hash < 0 ? target : target[..hash], out var entity))
                continue;

            var item = hash < 0 ? null : target[(hash + 1)..] + ".";
            var members = entity.Paths
                .Where(member => member.Stored && !member.Wire.Contains('*', StringComparison.Ordinal)
                    && (item is null ? member.CollectionAncestors == 0 : member.Wire.StartsWith(item, StringComparison.Ordinal) && member.CollectionAncestors == 1))
                .GroupBy(member => member.LeafKind == member.Kind ? member.Kind.ToString() : $"{member.Kind} of {member.LeafKind}")
                .Select(group => group.First())
                .ToList();

            stages.Add($"{stage} {kind} {alias} ({string.Join(", ", entities.Select(each => each!.GetValue<string>()))})");

            foreach (var member in members)
            {
                var path = item is null ? member.Wire : member.Wire[item.Length..];

                foreach (var op in Operators)
                    if (ValueOf(member) is { } value)
                        await CompareAsync(stage, kind, path, op, Operand(op, value), entities.Count > 1);

                await CompareAsync(stage, kind, path, "eq", member.LeafKind == Kind.String ? "1" : "\"x\"", entities.Count > 1);
            }

            await CompareAsync(stage, kind, "noSuchMember", "eq", "\"x\"", entities.Count > 1);
        }

        // An excuse is checked, not taken: no row of the run holds a record under an alias of the refused stage.
        foreach (var each in unreached)
        {
            var at = each.Explained.Body!["errors"]![0]!["stage"]!.GetValue<int>();
            var created = each.Explained.Body["stages"]![at]!["creates"]!.AsArray().Select(alias => alias!.GetValue<string>()).ToList();

            if (each.Run.StatusCode == 200 && each.Run.Items.OfType<JsonObject>().Any(row => created.Any(alias => row[alias] is JsonObject or JsonArray { Count: > 0 })))
                disagreements.Add($"{each.What}: explain refuses ({Said(each.Explained)}); a row of the run holds a record under {string.Join(", ", created)}, yet the run answered");
        }

        output.WriteLine($"{id}: {stages.Count} join stages over other services' rows [{string.Join("; ", stages)}], {compared} requests explained and run: {accepted} accepted, {refused} refused for how they bind with the same errors, {unreached.Count} refused by an owner the run's page did not reach");

        disagreements.Should().BeEmpty();
        unreached.Count.Should().BeLessThanOrEqualTo(compared / 4, "a parity that excuses most of what it compares shows nothing");

        if (stages.Count > 0)
        {
            accepted.Should().BePositive();
            refused.Should().BePositive();
        }
    }

    [Fact]
    public void P1_meets_conditions_at_owners_in_the_reference_set()
    {
        // The theory above shows nothing if no reference query has an alias that takes a condition.
        ExplainGolden.Cases.Keys.Should().Contain(["A3", "A4", "A5"], "their plain remote aliases (recipientContact, clerk) take conditions");
    }
}
