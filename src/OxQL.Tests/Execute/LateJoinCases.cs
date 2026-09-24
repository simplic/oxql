using MongoDB.Bson;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.TestCases;

/// <summary>
/// The late-join cases, shared by two projects: the unit tests run them over an evaluator of the
/// emitted stages, the integration tests run the same cases on a real server. The source lives
/// here and the integration project links it, so the two can never drift apart.
/// <para>
/// A join that runs after the page reads its local key off the page rows, so a projection
/// written between the join and the page must not take the key away: the rows carry the joined
/// value exactly as they would with the join where it was written.
/// </para>
/// </summary>
internal static class LateJoinCases
{
    /// <summary>The organisation every row belongs to and every case is scoped to.</summary>
    public static readonly Guid Organisation = Guid.Parse("a8d899a4-2029-4806-a4b7-a414eab21801");

    public const string Parent = "lj.parent";
    public const string Child = "lj.child";

    private static readonly BsonBinaryData Org = new(Organisation, GuidRepresentation.Standard);

    public static readonly EntityModel Model = ClrModelBuilder.Build(
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

    public static readonly Dictionary<string, BsonDocument[]> Collections = new(StringComparer.Ordinal)
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
            $$$"""[{"id":"{{{WireId(11)}}}","organizationId":"{{{Organisation}}}","title":"Child one","p":{"id":"{{{WireId(1)}}}","name":"Parent one"}},{"id":"{{{WireId(12)}}}","organizationId":"{{{Organisation}}}","title":"Child two","p":{"id":"{{{WireId(2)}}}","name":"Parent two"}},{"id":"{{{WireId(13)}}}","organizationId":"{{{Organisation}}}","title":"Child three","p":null}]"""
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
}
