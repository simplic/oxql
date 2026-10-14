using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.Core.Binding;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// The answer an owner gives per key (<c>keyedBy</c>, the internal form a join after the page asks
/// in): the first rows of each key by the record's key, at most as many as asked. Whatever keys
/// travel together, each key's rows are the rows it has when it is asked alone, and they are the
/// oracle's: for a member that is not the key (two employees of one user), and for the element of a
/// collection (the line of a shipment or a tour), where the key names an element and the row is its
/// holder. A batch of such queries answers each as it is answered alone.
/// </summary>
[Trait("Category", "Integration")]
public class KeyedWindowTests
{
    private static string Id(Guid id) => id.ToString("D");

    /// <summary>Runs <paramref name="call"/> at an owner of the fleet as a calling service's engine does: in a scope of the owner's host, under the caller's organisation.</summary>
    private static async Task<T> AtOwnerAsync<T>(LabService service, Func<IServiceProvider, Task<T>> call)
    {
        var host = await (await WeaveCorpus.FleetAsync()).Fleet.HostAsync(service);

        await using var scope = host.Services.CreateAsyncScope();

        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        context.Request.Headers[LabIdentity.ContractHeader] = "2";
        context.Request.Headers[LabIdentity.OrganisationHeader] = Id(Org.A.Id());
        context.Request.Headers[LabIdentity.UserHeader] = Id(LabIdentity.User);

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();

        accessor.HttpContext = context;

        try
        {
            return await call(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static QueryRequest Request(string entity, string path, IEnumerable<Guid> keys, int perKey, string project, int limit) =>
        JsonSerializer.Deserialize<QueryRequest>(new JsonObject
        {
            ["entityType"] = entity,
            ["keyedBy"] = new JsonObject { ["path"] = path, ["keys"] = new JsonArray(keys.Select(key => (JsonNode?)Id(key)).ToArray()), ["perKey"] = perKey },
            ["pipeline"] = JsonNode.Parse($$"""[{ "project": { {{project}} } }, { "page": { "limit": {{limit}} } }]"""),
        }.ToJsonString(), OxQLJson.Wire)!;

    /// <summary>The answers of a batch of owner queries, each as its rows.</summary>
    private static Task<List<List<JsonObject>>> BatchAsync(LabService service, IReadOnlyList<QueryRequest> queries) => AtOwnerAsync(service, async services =>
    {
        var outcome = await services.GetRequiredService<IOxQLQueryService>().BatchAsync(new BatchRequest { Queries = [.. queries] }, internalCall: true);
        var success = outcome.Should().BeOfType<BatchOutcome.Success>(outcome is BatchOutcome.Refused refused ? JsonSerializer.Serialize(refused.Refusal, OxQLJson.Wire) : "").Subject;
        var answer = JsonNode.Parse(JsonSerializer.Serialize(success.Response, OxQLJson.Wire))!;

        return answer["results"]!.AsArray().Select(result =>
        {
            result!["errors"].Should().BeNull(result.ToJsonString());

            return result["items"]!.AsArray().OfType<JsonObject>().ToList();
        }).ToList();
    });

    /// <summary>The keys asked together in windows of several sizes, each window in an order of its own.</summary>
    private static IEnumerable<List<Guid>> Windows(IReadOnlyList<Guid> keys)
    {
        yield return [.. keys];
        yield return [.. keys.Reverse()];

        foreach (var size in new[] { 2, 5, 16 })
            for (var from = 0; from < keys.Count; from += size)
                yield return [.. keys.Skip(from).Take(size)];
    }

    private sealed record Case(string Name, LabService Service, string Entity, string Path, string Project, IReadOnlyList<Guid> Keys, Func<JsonObject, Guid> KeyOf, Func<Guid, List<Guid>> Holders);

    private static Guid Key(JsonNode? node) => Guid.Parse(node!.GetValue<string>());

    private static readonly IReadOnlyList<Case> Cases =
    [
        new("the employees of a user", LabService.Staff, WeaveCorpus.Employee, "userId", """ "id": 1, "userId": 1 """,
            [.. Enumerable.Range(1, 9).Select(WeaveCorpus.UserId)],
            row => Key(row["userId"]),
            user => [.. WeaveCorpus.Employees.Where(employee => employee.UserId == user).Select(employee => employee.Id)]),

        new("the shipment that holds a line", LabService.Transport, WeaveCorpus.Shipment, "billingLines.id", """ "oxEl.id": 1, "oxEl.text": 1, "$default": 1 """,
            [.. WeaveCorpus.Shipments.SelectMany(shipment => shipment.BillingLines.Select(line => line.Id)).Concat(WeaveCorpus.Tours.SelectMany(tour => tour.BillingLines.Select(line => line.Id))).Distinct().Append(WeaveCorpus.SourceLineId(999))],
            row => Key(row["oxEl"]!["id"]),
            line => [.. WeaveCorpus.Shipments.Where(shipment => shipment.BillingLines.Any(held => held.Id == line)).Select(shipment => shipment.Id)]),

        new("the tour that holds a line", LabService.Transport, WeaveCorpus.Tour, "billingLines.id", """ "oxEl.id": 1, "$default": 1 """,
            [.. WeaveCorpus.Tours.SelectMany(tour => tour.BillingLines.Select(line => line.Id)).Concat(WeaveCorpus.Shipments.Take(6).SelectMany(shipment => shipment.BillingLines.Select(line => line.Id))).Distinct()],
            row => Key(row["oxEl"]!["id"]),
            line => [.. WeaveCorpus.Tours.Where(tour => tour.BillingLines.Any(held => held.Id == line)).Select(tour => tour.Id)]),
    ];

    public static TheoryData<int, int> Asked()
    {
        var data = new TheoryData<int, int>();

        for (var index = 0; index < Cases.Count; index++)
            foreach (var perKey in new[] { 1, 2, 3 })
                data.Add(index, perKey);

        return data;
    }

    /// <summary>What a key's rows have to be: its holders in the order of their keys, the first <paramref name="perKey"/> of them.</summary>
    private static List<string> Expected(Case asked, Guid key, int perKey) =>
        [.. asked.Holders(key).Select(Id).Order(StringComparer.Ordinal).Take(perKey)];

    [Theory]
    [MemberData(nameof(Asked))]
    public async Task The_rows_of_a_key_are_the_same_alone_and_in_every_window_and_they_are_its_first_holders(int index, int perKey)
    {
        var asked = Cases[index];
        var windows = Windows(asked.Keys).ToList();

        // Every key alone, in one batch of the owner's size at a time; then every window, in batches too.
        var singles = new Dictionary<Guid, List<string>>();

        foreach (var chunk in asked.Keys.Chunk(10))
        {
            var answers = await BatchAsync(asked.Service, [.. chunk.Select(key => Request(asked.Entity, asked.Path, [key], perKey, asked.Project, perKey))]);

            for (var at = 0; at < chunk.Length; at++)
            {
                answers[at].Should().OnlyContain(row => asked.KeyOf(row) == chunk[at], $"{asked.Name}: a key alone answers its own rows");
                singles[chunk[at]] = [.. answers[at].Select(row => row["id"]!.GetValue<string>())];
            }
        }

        foreach (var key in asked.Keys)
            singles[key].Should().Equal(Expected(asked, key, perKey), $"{asked.Name}, {perKey} per key: the key {key} alone");

        foreach (var chunk in windows.Chunk(10))
        {
            var answers = await BatchAsync(asked.Service, [.. chunk.Select(window => Request(asked.Entity, asked.Path, window, perKey, asked.Project, window.Count * perKey))]);

            for (var at = 0; at < chunk.Length; at++)
            {
                var byKey = answers[at].GroupBy(asked.KeyOf).ToDictionary(rows => rows.Key, rows => rows.Select(row => row["id"]!.GetValue<string>()).ToList());

                foreach (var key in chunk[at])
                    byKey.GetValueOrDefault(key, []).Should().Equal(singles[key], $"{asked.Name}, {perKey} per key: the key {key} in a window of {chunk[at].Count}");

                byKey.Keys.Should().BeSubsetOf(chunk[at], $"{asked.Name}: a window answers the keys it was asked");
            }
        }

        // The corpus holds a key with several holders, one with one and one with none, so each case was met.
        asked.Keys.Select(key => asked.Holders(key).Count).Should().Contain([0, 1]).And.Contain(count => count > 1);
    }

    /// <summary>The rows the engine answers when it is asked directly are the rows the batch answers.</summary>
    [Fact]
    public async Task A_query_per_key_answers_alone_what_it_answers_as_an_entry_of_a_batch()
    {
        var asked = Cases[1];
        var requests = Windows(asked.Keys).Take(6).Select(window => Request(asked.Entity, asked.Path, window, 2, asked.Project, window.Count * 2)).ToList();
        var batch = await BatchAsync(asked.Service, requests);

        for (var at = 0; at < requests.Count; at++)
        {
            var alone = await AtOwnerAsync(asked.Service, async services =>
            {
                var outcome = await services.GetRequiredService<IQueryEngine>().ExecuteAsync(requests[at], new RequestContext { Organisation = Org.A.Id(), Options = services.GetRequiredService<OxQLOptions>(), Internal = true });

                return outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result.Items.Select(item => item!.ToJsonString()).ToList();
            });

            batch[at].Select(row => row.ToJsonString()).Should().Equal(alone, $"entry {at}");
        }
    }
}
