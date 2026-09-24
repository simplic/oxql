using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// A join that runs after the page reads its local key off the page rows, so a projection
/// written between the join and the page must not take the key away: the rows carry the
/// joined value exactly as they would with the join where it was written. Each case runs the
/// real engine over an evaluator of the emitted stages, and again against a live server when
/// <c>OXQL_TEST_MONGO</c> names one.
/// </summary>
public class LateJoinExecutionTests
{
    private const string Parent = "lj.parent";
    private const string Child = "lj.child";

    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);

    private static readonly EntityModel Model = ClrModelBuilder.Build(
    [
        new EntityDeclaration(Parent, Parent, typeof(ParentModel), "tmp_lj_parents", null, false),
        new EntityDeclaration(Child, Child, typeof(ChildModel), "tmp_lj_children", null, false),
    ]);

    public sealed class ParentModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Code { get; set; } = "";

        public string Name { get; set; } = "";

        public string[] Tags { get; set; } = [];
    }

    public sealed class Holder
    {
        [OxQLReference(Parent)]
        public Guid Id { get; set; }
    }

    public sealed class ChildModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference(Parent)]
        public Guid? ParentId { get; set; }

        [OxQLReference(Parent, "code")]
        public string? ParentCode { get; set; }

        public Holder? Holder { get; set; }

        public string Title { get; set; } = "";
    }

    private static BsonBinaryData IdOf(int n) => new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), GuidRepresentation.Standard);

    private static string WireId(int n) => $"00000000-0000-0000-0000-{n:D12}";

    private static readonly BsonDocument[] Parents =
    [
        new() { ["_id"] = IdOf(1), ["OrganizationId"] = Org, ["Code"] = "P-ONE", ["Name"] = "Parent one", ["Tags"] = new BsonArray { "a", "b" } },
        new() { ["_id"] = IdOf(2), ["OrganizationId"] = Org, ["Code"] = "P-TWO", ["Name"] = "Parent two", ["Tags"] = new BsonArray { "c" } },
    ];

    // Child 13 names a parent that does not exist: a dangling key resolves to null.
    private static readonly BsonDocument[] Children =
    [
        new() { ["_id"] = IdOf(11), ["OrganizationId"] = Org, ["ParentId"] = IdOf(1), ["ParentCode"] = "P-ONE", ["Holder"] = new BsonDocument("_id", IdOf(1)), ["Title"] = "Child one" },
        new() { ["_id"] = IdOf(12), ["OrganizationId"] = Org, ["ParentId"] = IdOf(2), ["ParentCode"] = "P-TWO", ["Holder"] = new BsonDocument("_id", IdOf(2)), ["Title"] = "Child two" },
        new() { ["_id"] = IdOf(13), ["OrganizationId"] = Org, ["ParentId"] = IdOf(9), ["ParentCode"] = "P-NONE", ["Holder"] = new BsonDocument("_id", IdOf(9)), ["Title"] = "Child three" },
    ];

    private static readonly Dictionary<string, BsonDocument[]> Collections = new(StringComparer.Ordinal)
    {
        ["tmp_lj_parents"] = Parents,
        ["tmp_lj_children"] = Children,
    };

    /// <summary>The cases: the entity, the pipeline, and the rows it answers, as the wire writes them.</summary>
    public static TheoryData<string, string, string, string> Cases => new()
    {
        {
            "an inclusion that keeps the alias and not the key",
            Child,
            """[{ "resolve": { "path": "parentId", "as": "p", "select": ["name"] } }, { "project": { "id": 1, "p": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""",
            $$$"""[{"id":"{{{WireId(11)}}}","p":{"id":"{{{WireId(1)}}}","name":"Parent one"}},{"id":"{{{WireId(12)}}}","p":{"id":"{{{WireId(2)}}}","name":"Parent two"}},{"id":"{{{WireId(13)}}}","p":null}]"""
        },
        {
            "an exclusion of the key",
            Child,
            """[{ "resolve": { "path": "parentId", "as": "p", "select": ["name"] } }, { "project": { "parentId": 0, "parentCode": 0, "holder": 0 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""",
            $$$"""[{"id":"{{{WireId(11)}}}","organizationId":"{{{BindHost.Organisation}}}","title":"Child one","p":{"id":"{{{WireId(1)}}}","name":"Parent one"}},{"id":"{{{WireId(12)}}}","organizationId":"{{{BindHost.Organisation}}}","title":"Child two","p":{"id":"{{{WireId(2)}}}","name":"Parent two"}},{"id":"{{{WireId(13)}}}","organizationId":"{{{BindHost.Organisation}}}","title":"Child three","p":null}]"""
        },
        {
            "a string key an inclusion drops",
            Child,
            """[{ "resolve": { "path": "parentCode", "as": "p", "select": ["name"] } }, { "project": { "id": 1, "title": 1, "p": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""",
            $$$"""[{"id":"{{{WireId(11)}}}","title":"Child one","p":{"id":"{{{WireId(1)}}}","name":"Parent one"}},{"id":"{{{WireId(12)}}}","title":"Child two","p":{"id":"{{{WireId(2)}}}","name":"Parent two"}},{"id":"{{{WireId(13)}}}","title":"Child three","p":null}]"""
        },
        {
            "a nested key an inclusion drops with its parent member",
            Child,
            """[{ "resolve": { "path": "holder.id", "as": "p", "select": ["name"] } }, { "project": { "id": 1, "p": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""",
            $$$"""[{"id":"{{{WireId(11)}}}","p":{"id":"{{{WireId(1)}}}","name":"Parent one"}},{"id":"{{{WireId(12)}}}","p":{"id":"{{{WireId(2)}}}","name":"Parent two"}},{"id":"{{{WireId(13)}}}","p":null}]"""
        },
        {
            "a lookup whose parent key an unsorted unwound page excludes",
            Parent,
            """[{ "lookup": { "from": "lj.child", "path": "parentId", "as": "children", "select": ["title"] } }, { "unwind": { "path": "tags" } }, { "project": { "id": 0, "tags": 1, "children": 1 } }, { "page": { "limit": 5 } }]""",
            $$$"""[{"tags":"a","children":[{"id":"{{{WireId(11)}}}","title":"Child one"}]},{"tags":"b","children":[{"id":"{{{WireId(11)}}}","title":"Child one"}]},{"tags":"c","children":[{"id":"{{{WireId(12)}}}","title":"Child two"}]}]"""
        },
        {
            "a lookup whose parent key a sorted unwound page excludes",
            Parent,
            """[{ "lookup": { "from": "lj.child", "path": "parentId", "as": "children", "select": ["title"] } }, { "unwind": { "path": "tags" } }, { "project": { "id": 0, "tags": 1, "children": 1 } }, { "sort": [{ "tags": "desc" }] }, { "page": { "limit": 5 } }]""",
            $$$"""[{"tags":"c","children":[{"id":"{{{WireId(12)}}}","title":"Child two"}]},{"tags":"b","children":[{"id":"{{{WireId(11)}}}","title":"Child one"}]},{"tags":"a","children":[{"id":"{{{WireId(11)}}}","title":"Child one"}]}]"""
        },
    };

    private static async Task<(string Rows, CompiledQuery Compiled)> Run(IAggregateRunner runner, string entity, string pipeline)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Model), runner, BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExecuteAsync(BindHost.Request(entity, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        var compiled = MongoCompiler.Compile(await BindHost.BoundAsync(Model, entity, pipeline), new CompileOptions(10_000, null, 100_000));

        return (new JsonArray(((QueryOutcome.Success)outcome).Result.Items.Select(item => item?.DeepClone()).ToArray()).ToJsonString(), compiled);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_join_after_the_page_still_reads_its_key(string because, string entity, string pipeline, string expected)
    {
        var (rows, compiled) = await Run(new EvaluatingRunner(Collections), entity, pipeline);

        var kinds = compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToList();

        kinds.IndexOf("$lookup").Should().BeGreaterThan(kinds.IndexOf("$limit"), $"the join is display-only and runs after the page ({because})");
        rows.Should().Be(JsonNode.Parse(expected)!.ToJsonString(), because);
    }

    public static TheoryData<string, string, string, string> LiveCases => Cases;

    [LiveTheory]
    [MemberData(nameof(LiveCases))]
    public async Task A_join_after_the_page_still_reads_its_key_on_a_live_server(string because, string entity, string pipeline, string expected)
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable(LiveFactAttribute.ConnectionVariable));
        var databaseName = "tmp_lj_" + Guid.NewGuid().ToString("N");

        try
        {
            var database = client.GetDatabase(databaseName);

            foreach (var (name, documents) in Collections)
                await database.GetCollection<BsonDocument>(name).InsertManyAsync(documents.Select(document => document.DeepClone().AsBsonDocument));

            var (rows, _) = await Run(new MongoAggregateRunner(client, databaseName), entity, pipeline);

            rows.Should().Be(JsonNode.Parse(expected)!.ToJsonString(), because);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
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

            foreach (var field in set)
            {
                var elementAt = field.Value.AsBsonDocument["$arrayElemAt"].AsBsonArray;
                var array = Get(copy, elementAt[0].AsString[1..]) as BsonArray;
                var index = elementAt[1].ToInt32();

                // An index past the end leaves the field out, as the server does.
                if (array is not null && index < array.Count)
                    Set(copy, field.Name, array[index].DeepClone());
                else
                    Remove(copy, field.Name);
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

/// <summary>A theory that runs only against a live server, under the same variable as <see cref="LiveFactAttribute"/>.</summary>
internal sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveFactAttribute.ConnectionVariable)))
            Skip = $"Set {LiveFactAttribute.ConnectionVariable} to a connection string to run against a live server.";
    }
}
