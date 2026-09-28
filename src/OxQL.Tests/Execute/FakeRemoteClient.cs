using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.Tests.Execute;

/// <summary>
/// Stands in for the owners of remote entities: answers every query of a batch from a script
/// (rows, a refusal, or a fault), records every call with its budget, and reports which
/// services it knows.
/// </summary>
internal sealed class FakeRemoteClient : IRemoteQueryClient, IRemoteOwnerInfo
{
    /// <summary>What the client read of each owner's shallow health; an owner without an entry is unknown.</summary>
    public Dictionary<string, RemoteOwnerInfo> Owners { get; } = new(StringComparer.Ordinal);

    public RemoteOwnerInfo? OwnerOf(string serviceKey) => Owners.GetValueOrDefault(serviceKey);

    /// <summary>What one query is answered with.</summary>
    public abstract record Answer
    {
        /// <summary>Rows, as the owner's wire objects.</summary>
        public sealed record Rows(params JsonObject[] Items) : Answer;

        /// <summary>A refusal envelope; its one error at the owner's <paramref name="Stage"/> and <paramref name="Path"/>.</summary>
        public sealed record Refused(string Code, string Message, int Stage = 0, string? Path = null) : Answer;

        /// <summary>Rows with a page after them, reachable through <paramref name="NextCursor"/>.</summary>
        public sealed record Page(string NextCursor, params JsonObject[] Items) : Answer;

        /// <summary>A page that carries the count a semi-join's first call asks for.</summary>
        public sealed record Counted(long TotalCount, bool Capped, bool HasNextPage, params JsonObject[] Items) : Answer;
    }

    /// <summary>The answer per (service, query index in the batch); a service without an entry answers no rows.</summary>
    public Func<string, QueryRequest, int, Answer> Script { get; set; } = (_, _, _) => new Answer.Rows();

    /// <summary>Services that throw instead of answering.</summary>
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    /// <summary>Services that never answer within the budget.</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    /// <summary>Services the host knows; every service when null.</summary>
    public HashSet<string>? Configured { get; set; }

    /// <summary>Services that answer the health probe.</summary>
    public HashSet<string> Reachable { get; } = new(StringComparer.Ordinal);

    public List<(string Service, BatchRequest Request, TimeSpan Budget)> Calls { get; } = [];

    public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        Calls.Add((serviceKey, request, budget));

        if (Unreachable.Contains(serviceKey))
            throw new HttpRequestException($"'{serviceKey}' is not reachable.");

        if (Silent.Contains(serviceKey))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        var results = new List<JsonNode?>();

        for (var index = 0; index < request.Queries.Count; index++)
        {
            results.Add(Script(serviceKey, request.Queries[index], index) switch
            {
                Answer.Rows rows => new JsonObject
                {
                    ["items"] = new JsonArray(rows.Items.Select(item => (JsonNode)item.DeepClone()).ToArray()),
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = false },
                },
                Answer.Page page => new JsonObject
                {
                    ["items"] = new JsonArray(page.Items.Select(item => (JsonNode)item.DeepClone()).ToArray()),
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = true, ["nextCursor"] = page.NextCursor },
                },
                Answer.Counted counted => new JsonObject
                {
                    ["items"] = new JsonArray(counted.Items.Select(item => (JsonNode)item.DeepClone()).ToArray()),
                    ["pageInfo"] = new JsonObject
                    {
                        ["hasNextPage"] = counted.HasNextPage,
                        ["totalCount"] = counted.TotalCount,
                        ["totalCountCapped"] = counted.Capped,
                    },
                },
                Answer.Refused refused => new JsonObject
                {
                    ["type"] = "validation_error",
                    ["title"] = "The request could not be bound.",
                    ["errors"] = new JsonArray(new JsonObject { ["code"] = refused.Code, ["message"] = refused.Message, ["stage"] = refused.Stage, ["path"] = refused.Path }),
                },
                _ => null,
            });
        }

        return new BatchResponse { Results = results };
    }

    public bool IsConfigured(string serviceKey) => Configured is null || Configured.Contains(serviceKey);

    /// <summary>How many reachability probes were answered; the health cache is measured by it.</summary>
    public int Reachability { get; private set; }

    public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken)
    {
        Reachability++;

        return Task.FromResult(Reachable.Contains(serviceKey));
    }

    /// <summary>An owner row: the target field plus the members named.</summary>
    public static JsonObject Row(string field, string key, params (string Name, object? Value)[] members)
    {
        var row = new JsonObject { [field] = key };

        foreach (var (name, value) in members)
            row[name] = value is null ? null : JsonSerializer.SerializeToNode(value);

        return row;
    }
}
