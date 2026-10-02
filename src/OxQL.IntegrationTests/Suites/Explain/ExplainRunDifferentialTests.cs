using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// T1, the fleet differential (improvement plan §3.T, P12 "explain ≡ execution"): over the fleet's real
/// hosts, what explain says of a request is what running it does.
/// <list type="bullet">
///   <item>The reference set (the report scenarios and the golden chains) and every route this suite
///   derives from the fleet's own reference graph, from five source entities: one hop along each
///   declared reference, a second hop along each reference of what the first reaches (under the alias,
///   and under the owning row of an item target), and, where the first hop reaches several entities
///   that each have a reference of their own, the union join over them. The stages are the ones a
///   route finder writes: <c>resolve { path, as, parentAs?, forTarget? | byTarget, elements?, onMissing: "report" }</c>,
///   no <c>select</c>, no projection.</item>
///   <item>A run that is refused for how the request binds (400, or 422 <c>RESOLVE_REFUSED</c> /
///   <c>RESOLVE_UNAVAILABLE</c>) is a request explain calls not valid, and a request explain refuses
///   for what this host binds is a run that is refused. Where explain is not valid only for what an
///   owner refused, the run is refused once its page reaches that owner; a page that holds no row
///   for that owner asks it nothing, so the run answers. That excuse is checked, not taken
///   (<see cref="ExcuseAsync"/>): the run's rows must hold no record of the refusing target under the
///   alias the refused stage continues from, and the owner, asked the refused stage directly as a
///   request of its own, must refuse it with the same code. Such requests are counted and bounded.</item>
///   <item>The entity an owning row names (<c>parentAs.entity</c>) on every row is one of the entities
///   explain lists for that alias.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class ExplainRunDifferentialTests(ITestOutputHelper output)
{
    /// <summary>The entities the routes start at.</summary>
    private static readonly string[] Sources = ["ledger.transaction", "ledger.billing_line", "transport.shipment", "transport.tour", "transport.delivery_attempt"];

    private static readonly Lazy<IReadOnlyDictionary<string, EntityDef>> Entities = new(() =>
        LabService.All.SelectMany(service => service.Model.Entities.Values).ToDictionary(entity => entity.Id, StringComparer.Ordinal));

    /// <summary>A reference a route can follow from a row of an entity (or of an element of one of its item collections).</summary>
    private sealed record Hop(string Path, bool Elements, IReadOnlyList<ReferenceTarget> Targets);

    /// <summary>
    /// The references under <paramref name="entity"/> a resolve can follow in one stage: stored paths
    /// crossing at most one collection (two are <c>UNWIND_ORDER</c>), relative to the element when
    /// <paramref name="item"/> names an item collection (then only the paths below it), else every path.
    /// </summary>
    private static IEnumerable<Hop> HopsOf(EntityDef entity, string? item)
    {
        foreach (var path in entity.Paths)
        {
            if (path.References.Count == 0 || !path.Stored || path.Wire.Contains('*', StringComparison.Ordinal))
                continue;

            var wire = path.Wire;
            var collections = path.CollectionAncestors + (path.Kind == Kind.Array ? 1 : 0);

            if (item is not null)
            {
                if (!wire.StartsWith(item + ".", StringComparison.Ordinal))
                    continue;

                wire = wire[(item.Length + 1)..];
                collections--;
            }

            if (collections > 1)
                continue;

            yield return new Hop(wire, collections == 1, path.References.SelectMany(reference => reference.Targets).DistinctBy(target => target.ToString()).ToList());
        }
    }

    private static JsonObject Resolve(string path, string alias, bool elements, string? parentAs = null, string? forTarget = null)
    {
        var resolve = new JsonObject { ["path"] = path, ["as"] = alias, ["onMissing"] = "report" };

        if (elements)
            resolve["elements"] = "first";

        if (parentAs is not null)
            resolve["parentAs"] = parentAs;

        if (forTarget is not null)
            resolve["forTarget"] = forTarget;

        return new JsonObject { ["resolve"] = resolve };
    }

    private static JsonObject Request(string source, params JsonObject[] stages) => new()
    {
        ["entityType"] = source,
        ["pipeline"] = new JsonArray([.. stages.Select(stage => (JsonNode?)stage), new JsonObject { ["page"] = new JsonObject { ["limit"] = 25 } }]),
    };

    /// <summary>Every route of at most two hops from the source entities, each a request with its name.</summary>
    private static List<(string Name, JsonObject Request)> Routes()
    {
        var routes = new List<(string, JsonObject)>();

        foreach (var source in Sources)
        {
            var entity = Entities.Value[source];

            foreach (var first in HopsOf(entity, null))
            {
                var owning = first.Targets.Any(target => target.Item is not null) ? "p1" : null;
                var one = Resolve(first.Path, "a1", first.Elements, owning);

                routes.Add(($"{source}: {first.Path}", Request(source, one)));

                var union = first.Targets.Select(target => target.Entity).Distinct(StringComparer.Ordinal).Count() > 1;
                var branches = new JsonObject();

                // What a route finder never writes, so that the differential also meets requests that do not
                // bind: a second hop along a member that declares no reference, and, under a union, one for a
                // target the first hop does not have.
                routes.Add(($"{source}: {first.Path} -> a1.id (no reference)", Request(source, (JsonObject)one.DeepClone(), Resolve("a1.id", "a2", false))));

                if (union)
                    routes.Add(($"{source}: {first.Path} -> forTarget of no target", Request(source, (JsonObject)one.DeepClone(), Resolve("a1.id", "a2", false, null, source))));

                foreach (var target in first.Targets)
                {
                    if (!Entities.Value.TryGetValue(target.Entity, out var reached))
                        continue;

                    // Under the alias: the references of the record, or of the element of an item target.
                    foreach (var second in HopsOf(reached, target.Item))
                    {
                        routes.Add(($"{source}: {first.Path} -> [{target}] a1.{second.Path}",
                            Request(source, (JsonObject)one.DeepClone(), Resolve("a1." + second.Path, "a2", second.Elements, second.Targets.Any(next => next.Item is not null) ? "p2" : null, union ? target.Entity : null))));

                        // The union join: one alias across the targets, each by its own first reference.
                        if (union && !branches.ContainsKey(target.Entity) && !second.Elements)
                            branches[target.Entity] = "a1." + second.Path;
                    }

                    // Under the owning row of an item target: the references of the entity that holds the item.
                    if (target.Item is not null)
                        foreach (var second in HopsOf(reached, null))
                            routes.Add(($"{source}: {first.Path} -> [{target}] p1.{second.Path}",
                                Request(source, (JsonObject)one.DeepClone(), Resolve("p1." + second.Path, "a2", second.Elements, second.Targets.Any(next => next.Item is not null) ? "p2" : null, union ? target.Entity : null))));
                }

                if (branches.Count > 1)
                    routes.Add(($"{source}: {first.Path} -> byTarget", Request(source, (JsonObject)one.DeepClone(),
                        new JsonObject { ["resolve"] = new JsonObject { ["as"] = "a2", ["byTarget"] = branches, ["onMissing"] = "report" } })));
            }
        }

        return routes;
    }

    private static bool BindRefused(WireAnswer run) =>
        run.Status == HttpStatusCode.BadRequest
        || (run.StatusCode == 422 && run.ErrorCodes.FirstOrDefault() is "RESOLVE_REFUSED" or "RESOLVE_UNAVAILABLE");

    /// <summary>A request explain calls not valid only for what an owner refuses, while its run answers: to be excused, or not.</summary>
    private sealed record Unreached(string Name, JsonObject Request, WireAnswer Explained, WireAnswer Run);

    /// <summary>
    /// Whether a run that answers although explain is not valid is excused: explain's errors all come
    /// from owners, and for each of them
    /// <list type="number">
    ///   <item>the page asked that owner nothing for the refused stage: no row of the run holds a record
    ///   of the refusing target under the alias the stage continues from (a row that did would have sent
    ///   the stage to the owner, and the run would be refused);</item>
    ///   <item>the owner really refuses the stage: explained at the owner's own host as a request over
    ///   the refusing entity, the stage is not valid there, with the code explain passed on.</item>
    /// </list>
    /// Null when excused; else what does not hold. An error this check cannot take apart (no stage, no
    /// alias to continue from, a stage that cannot be asked directly) is not excused: the differential
    /// must then be taught the new form instead of passing it.
    /// </summary>
    private static async Task<string?> ExcuseAsync(Unreached unreached)
    {
        var (name, request, explained, run) = unreached;
        var body = explained.Body!;
        var aliases = body["aliases"]!.AsObject();

        foreach (var error in body["errors"]!.AsArray().OfType<JsonObject>())
        {
            var owner = error["params"]!["owner"]!.AsObject();
            var said = $"{error["code"]}@{error["stage"]} {error["path"]}";

            if (error["stage"] is not JsonValue at || !at.TryGetValue<int>(out var stage)
                || owner["entity"]?.GetValue<string>() is not { } entity || owner["target"]?.GetValue<string>() is not { } target)
                return $"{name}: the owner error {said} names no stage or no target, so it cannot be checked";

            // The aliases whose rows are the refusing target's and under which the refused stage continues.
            var anchors = aliases
                .Where(alias => alias.Value?["targets"] is JsonArray targets && targets.OfType<JsonObject>().Any(each =>
                    each["target"]?.GetValue<string>() == target && each["continued"] is JsonArray continued && continued.Any(index => index!.GetValue<int>() == stage)))
                .Select(alias => alias.Key)
                .ToList();

            if (anchors.Count != 1)
                return $"{name}: the owner error {said} continues from {anchors.Count} aliases of target '{target}', so it cannot be checked";

            var anchor = anchors[0];
            var owning = aliases.FirstOrDefault(alias => alias.Value?["parentOf"]?.GetValue<string>() == anchor).Key;
            var several = aliases[anchor]!["targets"]!.AsArray().Count > 1;

            // 1. The page reached no record of the refusing target.
            foreach (var row in run.Items.OfType<JsonObject>())
            {
                if (row[anchor] is null or JsonArray { Count: 0 })
                    continue;

                // Which target a row of a union came from is the owning row's to say; without one, any record counts.
                if (several && owning is not null && row[owning]?["entity"]?.GetValue<string>() is { } from && from != entity)
                    continue;

                return $"{name}: explain says '{owner["service"]}' refuses stage {stage} for '{target}' ({said}); a row of the run holds such a record under '{anchor}', yet the run answered {run.StatusCode}";
            }

            // 2. The owner refuses the stage when it is asked directly.
            if (request["pipeline"]![stage]?["resolve"] is not JsonObject written || written["path"]?.GetValue<string>() is not { } path
                || target.Contains('#', StringComparison.Ordinal) || !path.StartsWith(anchor + ".", StringComparison.Ordinal))
                return $"{name}: the stage of the owner error {said} cannot be asked of '{entity}' directly (an item target, a union join or a path under another alias); teach the differential this form";

            var direct = (JsonObject)written.DeepClone();

            direct["path"] = path[(anchor.Length + 1)..];
            direct.Remove("forTarget");

            var asked = await (await Lab.ClientAsync(LabService.Of(entity.Split('.')[0]), Org.R)).ExplainHereAsync(Request(entity, new JsonObject { ["resolve"] = direct }));

            if (asked.StatusCode != 200 || asked.Body!["valid"]!.GetValue<bool>()
                || !asked.Body["errors"]!.AsArray().OfType<JsonObject>().Any(refusal => refusal["code"]?.GetValue<string>() == error["code"]?.GetValue<string>()))
                return $"{name}: explain says '{owner["service"]}' refuses stage {stage} for '{target}' ({said}), but '{entity}' asked the stage directly answers {asked}";
        }

        return null;
    }

    /// <summary>What one request's explain and run say of each other; null when they agree, or when only an owner's refusal the page may not have reached stands between them (added to <paramref name="unreached"/>, to be checked).</summary>
    private static string? Disagreement(string name, JsonObject request, WireAnswer explained, WireAnswer run, List<Unreached> unreached)
    {
        if (explained.StatusCode != 200)
            return run.StatusCode == explained.StatusCode ? null : $"{name}: explain answered {explained.StatusCode}, the run {run.StatusCode}";

        var valid = explained.Body!["valid"]!.GetValue<bool>();
        var refused = BindRefused(run);
        var errors = explained.Body["errors"]!.AsArray().OfType<JsonObject>().ToList();

        if (valid && refused)
            return $"{name}: explain is valid, the run is refused: {run}";

        if (!valid && !refused)
        {
            // What only an owner refuses, a page that asks that owner nothing does not meet: checked by the caller.
            if (errors.Count > 0 && errors.All(error => error["params"]?["owner"] is JsonObject))
            {
                unreached.Add(new Unreached(name, request, explained, run));
                return null;
            }

            return $"{name}: explain is not valid ({string.Join("; ", errors.Select(error => $"{error["code"]}@{error["stage"]} {error["path"]}"))}), the run answered {run.StatusCode}";
        }

        if (run.StatusCode != 200)
            return null;

        // The entity an owning row names is one explain lists for the alias.
        foreach (var (alias, described) in explained.Body["aliases"]!.AsObject())
        {
            if (described?["parentOf"] is null)
                continue;

            var listed = described["entities"]!.AsArray().Select(entity => entity!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

            foreach (var row in run.Items.OfType<JsonObject>())
                foreach (var owning in row[alias] is JsonArray many ? many.OfType<JsonObject>() : row[alias] is JsonObject single ? [single] : [])
                    if (owning["entity"]?.GetValue<string>() is { } entity && !listed.Contains(entity))
                        return $"{name}: a row's '{alias}.entity' is '{entity}', which explain does not list ({string.Join(", ", listed)})";
        }

        return null;
    }

    [Fact]
    public async Task T1_explain_and_the_run_agree_on_the_reference_set()
    {
        var disagreements = new List<string>();
        var unreached = new List<Unreached>();

        foreach (var (id, build) in ExplainGolden.Cases)
        {
            var request = build();
            var client = await Report.ReportScenarios.ClientAsync(request);

            if (Disagreement(id, request, await client.ExplainHereAsync(request), await client.QueryAsync(request), unreached) is { } disagreement)
                disagreements.Add(disagreement);
        }

        output.WriteLine($"{ExplainGolden.Cases.Count} requests of the reference set; {unreached.Count} not valid only for what an owner refuses that the page did not reach");
        disagreements.Should().BeEmpty();
        unreached.Should().BeEmpty("the reference set's pages reach every owner its requests continue at: what an owner refuses there, the run is refused for");
    }

    [Fact]
    public async Task T1_explain_and_the_run_agree_on_every_route_from_five_source_entities()
    {
        var routes = Routes();
        var disagreements = new List<string>();
        var valid = 0;
        var refused = 0;
        var unreached = new List<Unreached>();
        var owning = 0;

        foreach (var (name, request) in routes)
        {
            var client = await Lab.ClientAsync(LabService.Of(request["entityType"]!.GetValue<string>().Split('.')[0]), Org.R);
            var explained = await client.ExplainHereAsync(request);
            var run = await client.QueryAsync(request);

            if (explained.StatusCode == 200 && explained.Body!["valid"]!.GetValue<bool>())
                valid++;

            if (BindRefused(run))
                refused++;

            if (explained.StatusCode == 200 && explained.Body!["aliases"]!.AsObject().Any(alias => alias.Value?["parentOf"] is not null))
                owning++;

            if (Disagreement(name, request, explained, run, unreached) is { } disagreement)
                disagreements.Add(disagreement);
        }

        // The excuse of each such route is checked: the page reached no record of the refusing target, and the owner refuses the stage.
        foreach (var each in unreached)
            if (await ExcuseAsync(each) is { } unexcused)
                disagreements.Add(unexcused);

        output.WriteLine($"{routes.Count} routes from {Sources.Length} source entities: {valid} valid, {refused} refused by a run for how they bind, {unreached.Count} not valid only for what an owner refuses that the page did not reach (each checked at its owner), {owning} with an owning row");

        foreach (var group in routes.GroupBy(route => route.Name.Split(':')[0]))
            output.WriteLine($"  {group.Key}: {group.Count()}");

        routes.Count.Should().BeGreaterThan(40, "the fleet's reference graph gives the differential something to walk");
        valid.Should().BeGreaterThan(routes.Count / 2, "the routes a route finder writes bind");
        (routes.Count - valid).Should().BeGreaterThan(10, "and the ones it never writes do not, at explain and at the run alike");
        disagreements.Should().BeEmpty();
        unreached.Count.Should().BeLessThanOrEqualTo(routes.Count / 8, "the routes whose run cannot confirm explain stay the exception: a differential that excuses most of what it compares shows nothing");
    }
}
