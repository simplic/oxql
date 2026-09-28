using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.TestCases;
using Xunit;
using static OxQL.TestCases.LateJoinCases;

namespace OxQL.Tests.Execute;

/// <summary>
/// A join that runs after the page reads its local key off the page rows, so a projection
/// written between the join and the page must not take the key away: the rows carry the
/// joined value exactly as they would with the join where it was written. Each case runs the
/// real engine over an evaluator of the emitted stages; the integration tests run the same
/// cases (<see cref="LateJoinCases"/>) on a real server.
/// </summary>
public class LateJoinExecutionTests
{
    private static async Task<(string Rows, CompiledQuery Compiled)> Run(IAggregateRunner runner, string entity, string pipeline)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(LateJoinCases.Model), runner, BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExecuteAsync(BindHost.Request(entity, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        var compiled = MongoCompiler.Compile(await BindHost.BoundAsync(LateJoinCases.Model, entity, pipeline), new CompileOptions(10_000, null, 100_000));

        return (new JsonArray(((QueryOutcome.Success)outcome).Result.Items.Select(item => item?.DeepClone()).ToArray()).ToJsonString(), compiled);
    }

    [Theory]
    [MemberData(nameof(LateJoinCases.Cases), MemberType = typeof(LateJoinCases))]
    public async Task A_join_after_the_page_still_reads_its_key(string because, string entity, string pipeline, string expected)
    {
        var (rows, compiled) = await Run(new EvaluatingRunner(Collections), entity, pipeline);

        var kinds = compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToList();

        kinds.IndexOf("$lookup").Should().BeGreaterThan(kinds.IndexOf("$limit"), $"the join is display-only and runs after the page ({because})");
        rows.Should().Be(JsonNode.Parse(expected)!.ToJsonString(), because);
    }


    /// <summary>
    /// Evaluates the stages the compiler emits for these cases over in-memory collections:
    /// scope matches, sort, skip, limit, projection, unwind, join, set and unset. A stage or an
    /// operator outside that set fails the test rather than being skipped.
    /// </summary>
    private sealed class EvaluatingRunner(IReadOnlyDictionary<string, BsonDocument[]> collections) : IAggregateRunner
    {
        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BsonDocument>>(Evaluate(collections[entity.Collection].Select(document => document.DeepClone().AsBsonDocument).ToList(), stages));

        private List<BsonDocument> Evaluate(List<BsonDocument> rows, IEnumerable<BsonDocument> stages)
        {
            foreach (var stage in stages)
            {
                var element = stage.GetElement(0);

                rows = element.Name switch
                {
                    "$match" => rows.Where(row => Matches(row, element.Value.AsBsonDocument)).ToList(),
                    "$sort" => SortRows(rows, element.Value.AsBsonDocument),
                    "$skip" => rows.Skip(element.Value.ToInt32()).ToList(),
                    "$limit" => rows.Take(element.Value.ToInt32()).ToList(),
                    "$project" => rows.Select(row => ProjectRow(row, element.Value.AsBsonDocument)).ToList(),
                    "$unwind" => rows.SelectMany(row => UnwindRow(row, element.Value.AsBsonDocument)).ToList(),
                    "$lookup" => rows.Select(row => Join(row, element.Value.AsBsonDocument)).ToList(),
                    "$set" => rows.Select(row => SetRow(row, element.Value.AsBsonDocument)).ToList(),
                    "$unset" => rows.Select(row => UnsetRow(row, element.Value)).ToList(),
                    "$count" => [new BsonDocument(element.Value.AsString, rows.Count)],
                    _ => throw new NotSupportedException($"The evaluator does not run {element.Name}."),
                };
            }

            return rows;
        }

        private static bool Matches(BsonDocument row, BsonDocument filter) => filter.All(element => element.Name switch
        {
            "$and" => element.Value.AsBsonArray.All(inner => Matches(row, inner.AsBsonDocument)),
            _ when element.Name.StartsWith('$') || element.Value is BsonDocument => throw new NotSupportedException($"The evaluator does not match {element.ToJson()}."),
            _ => Get(row, element.Name) == element.Value,
        });

        private static List<BsonDocument> SortRows(List<BsonDocument> rows, BsonDocument sort)
        {
            var list = rows.ToList();

            list.Sort((left, right) =>
            {
                foreach (var field in sort)
                {
                    var order = (Get(left, field.Name) ?? BsonNull.Value).CompareTo(Get(right, field.Name) ?? BsonNull.Value);

                    if (order != 0)
                        return field.Value.ToInt32() < 0 ? -order : order;
                }

                return 0;
            });

            return list;
        }

        private static BsonDocument ProjectRow(BsonDocument row, BsonDocument projection)
        {
            var inclusion = projection.Any(field => field.Name != "_id" && field.Value.ToInt32() != 0);

            if (!inclusion)
            {
                var copy = row.DeepClone().AsBsonDocument;

                foreach (var field in projection)
                    Remove(copy, field.Name);

                return copy;
            }

            var paths = projection.Where(field => field.Value.ToInt32() != 0).Select(field => field.Name).ToList();

            if (!projection.Contains("_id"))
                paths.Add("_id");

            return Include(row, paths);
        }

        private static BsonDocument Include(BsonDocument document, IReadOnlyList<string> paths)
        {
            var result = new BsonDocument();

            foreach (var element in document)
            {
                if (paths.Contains(element.Name))
                {
                    result[element.Name] = element.Value.DeepClone();
                    continue;
                }

                var below = paths.Where(path => path.StartsWith(element.Name + ".", StringComparison.Ordinal)).Select(path => path[(element.Name.Length + 1)..]).ToList();

                if (below.Count > 0 && element.Value is BsonDocument inner)
                    result[element.Name] = Include(inner, below);
            }

            return result;
        }

        private static IEnumerable<BsonDocument> UnwindRow(BsonDocument row, BsonDocument unwind)
        {
            var path = unwind["path"].AsString[1..];

            if (Get(row, path) is not BsonArray array || array.Count == 0)
            {
                if (unwind.GetValue("preserveNullAndEmptyArrays", false).ToBoolean())
                    yield return row;

                yield break;
            }

            for (var index = 0; index < array.Count; index++)
            {
                var copy = row.DeepClone().AsBsonDocument;

                Set(copy, path, array[index].DeepClone());

                if (unwind.TryGetValue("includeArrayIndex", out var indexField))
                    Set(copy, indexField.AsString, (long)index);

                yield return copy;
            }
        }

        private BsonDocument Join(BsonDocument row, BsonDocument join)
        {
            // A missing local key compares as null, the way the server reads it.
            var local = Get(row, join["localField"].AsString) ?? BsonNull.Value;
            var foreignField = join["foreignField"].AsString;
            var candidates = collections[join["from"].AsString]
                .Where(document => (Get(document, foreignField) ?? BsonNull.Value) == local)
                .Select(document => document.DeepClone().AsBsonDocument)
                .ToList();

            var copy = row.DeepClone().AsBsonDocument;

            copy[join["as"].AsString] = new BsonArray(Evaluate(candidates, join["pipeline"].AsBsonArray.Select(stage => stage.AsBsonDocument)));

            return copy;
        }

        private static BsonDocument SetRow(BsonDocument row, BsonDocument set)
        {
            var copy = row.DeepClone().AsBsonDocument;

            // Every expression reads the row as it came in, as the server's $set does.
            foreach (var field in set)
            {
                var expression = field.Value.AsBsonDocument.GetElement(0);
                var operands = expression.Value.AsBsonArray;

                switch (expression.Name)
                {
                    case "$arrayElemAt":
                    {
                        var array = Get(row, operands[0].AsString[1..]) as BsonArray;
                        var index = operands[1].ToInt32();

                        // An index past the end leaves the field out, as the server does.
                        if (array is not null && index < array.Count)
                            Set(copy, field.Name, array[index].DeepClone());
                        else
                            Remove(copy, field.Name);
                        break;
                    }

                    // A lookup's cut to its limit.
                    case "$slice":
                        Set(copy, field.Name, new BsonArray(((BsonArray)Get(row, operands[0].AsString[1..])!).Take(operands[1].ToInt32()).Select(item => item.DeepClone())));
                        break;

                    // A lookup's truncation flag: { $gt: [ { $size: "$alias" }, limit ] }.
                    case "$gt":
                        Set(copy, field.Name, ((BsonArray)Get(row, operands[0].AsBsonDocument["$size"].AsString[1..])!).Count > operands[1].ToInt32());
                        break;

                    default:
                        throw new NotSupportedException($"The evaluator does not set {expression.Name}.");
                }
            }

            return copy;
        }

        private static BsonDocument UnsetRow(BsonDocument row, BsonValue unset)
        {
            var copy = row.DeepClone().AsBsonDocument;

            foreach (var path in unset is BsonArray paths ? paths.Select(path => path.AsString) : [unset.AsString])
                Remove(copy, path);

            return copy;
        }

        private static BsonValue? Get(BsonDocument document, string path)
        {
            BsonValue current = document;

            foreach (var part in path.Split('.'))
            {
                if (current is not BsonDocument inner || !inner.TryGetValue(part, out current!))
                    return null;
            }

            return current;
        }

        private static void Set(BsonDocument document, string path, BsonValue value)
        {
            var parts = path.Split('.');
            var current = document;

            foreach (var part in parts[..^1])
            {
                if (!current.TryGetValue(part, out var next) || next is not BsonDocument inner)
                    current[part] = inner = new BsonDocument();

                current = inner;
            }

            current[parts[^1]] = value;
        }

        private static void Remove(BsonDocument document, string path)
        {
            var parts = path.Split('.');
            var current = document;

            foreach (var part in parts[..^1])
            {
                if (!current.TryGetValue(part, out var next) || next is not BsonDocument inner)
                    return;

                current = inner;
            }

            current.Remove(parts[^1]);
        }
    }
}
