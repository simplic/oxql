using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// A join in the aggregate over a reference that names its target by another member than the
/// target's key (a folder by its <c>code</c>): a reference that holds nothing names no record, and a
/// record whose member holds nothing has no children, whatever the other side holds. The rows that
/// hold a key join as they do everywhere, and the outcome of the join says the same as the alias.
/// </summary>
[Trait("Category", "Integration")]
public class AbsentKeyJoinServerTests
{
    private static readonly EngineDirect Joins = new(CaseInsensitiveJoinModel.Model);

    private static BsonDocument Folder(int id, BsonValue? code)
    {
        var folder = new BsonDocument { ["_id"] = new BsonBinaryData(Guid.Parse($"10000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Name"] = "folder " + id };

        if (code is not null)
            folder["Code"] = code;

        return folder;
    }

    private static BsonDocument Document(int id, BsonValue? code)
    {
        var document = new BsonDocument { ["_id"] = new BsonBinaryData(Guid.Parse($"20000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Title"] = "title " + id };

        if (code is not null)
            document["FolderCode"] = code;

        return document;
    }

    private static async Task<(TestDatabase Owned, OxQL.Core.Engine.IQueryEngine Engine)> SeedAsync(string purpose)
    {
        var owned = await MongoFixture.CreateDatabaseAsync(purpose);

        // Folders 3 and 4 hold no code; documents 2 and 3 name no folder.
        await owned.Database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.FolderCollection).InsertManyAsync([Folder(1, "abc"), Folder(2, "xyz"), Folder(3, BsonNull.Value), Folder(4, null)]);
        await owned.Database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.DocumentCollection).InsertManyAsync([Document(1, "abc"), Document(2, BsonNull.Value), Document(3, null), Document(4, "xyz"), Document(5, "abc"), Document(6, "none")]);

        return (owned, Joins.Engine(await MongoFixture.ClientAsync(), owned.Name));
    }

    /// <summary>With and without a collation, read by a later stage (in the aggregate before the page) and by the row alone (after it).</summary>
    [Theory]
    [InlineData("""{ "sort": [{ "id": "asc" }] }, { "project": { "title": 1, "folder.name": 1, "found": 1 } }""")]
    [InlineData("""{ "match": { "title": { "startsWith": "TITLE" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "title": 1, "folder.name": 1, "found": 1 } }""")]
    [InlineData("""{ "match": { "or": [{ "folder.name": { "neq": "nobody" } }, { "found": { "neq": "resolved" } }] } }, { "sort": [{ "id": "asc" }] }, { "project": { "title": 1, "folder.name": 1, "found": 1 } }""")]
    public async Task A_reference_that_holds_nothing_resolves_to_no_record_where_a_record_holds_nothing_in_the_member_it_is_named_by(string stages)
    {
        var (owned, engine) = await SeedAsync("absent_key_resolve");

        await using (owned)
        {
            foreach (var resolve in new[] { """{ "resolve": { "path": "folderCode", "as": "folder", "outcomeAs": "found" } }""", """{ "resolve": { "path": "folderCode", "as": "folder", "outcomeAs": "found", "filter": { "name": { "neq": "folder 2" } } } }""" })
            {
                var rows = await Joins.ExecuteAsync(engine, CaseInsensitiveJoinModel.Document, $"[{resolve}, {stages}]");
                var filtered = resolve.Contains("filter", StringComparison.Ordinal);

                rows.Select(row => row!["folder"]?["name"]?.GetValue<string>()).Should().Equal(["folder 1", null, null, filtered ? null : "folder 2", "folder 1", null], resolve);
                rows.Select(row => row!["found"]!.GetValue<string>()).Should().Equal(["resolved", "reference_null", "reference_null", filtered ? "excluded" : "resolved", "resolved", "not_found"], resolve);
            }
        }
    }

    [Theory]
    [InlineData("""{ "sort": [{ "id": "asc" }] }, { "project": { "name": 1, "docs.title": 1 } }""")]
    [InlineData("""{ "match": { "name": { "startsWith": "FOLDER" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "name": 1, "docs.title": 1 } }""")]
    [InlineData("""{ "match": { "docs.title": { "neq": "nothing" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "name": 1, "docs.title": 1 } }""")]
    public async Task A_record_that_holds_nothing_in_the_member_it_is_named_by_has_no_children(string stages)
    {
        var (owned, engine) = await SeedAsync("absent_key_lookup");

        await using (owned)
        {
            var rows = await Joins.ExecuteAsync(engine, CaseInsensitiveJoinModel.Folder, $$"""[{ "lookup": { "from": "ci.document", "path": "folderCode", "as": "docs", "sort": [{ "title": "asc" }] } }, {{stages}}]""");

            rows.Select(row => string.Join(",", row!["docs"]!.AsArray().Select(document => document!["title"]!.GetValue<string>())))
                .Should().Equal("title 1,title 5", "title 4", "", "");

            var first = await Joins.ExecuteAsync(engine, CaseInsensitiveJoinModel.Folder, $$"""[{ "lookup": { "from": "ci.document", "path": "folderCode", "as": "doc", "first": true } }, { "sort": [{ "id": "asc" }] }, { "project": { "name": 1, "doc.title": 1 } }]""");

            first.Select(row => row!["doc"]?["title"]?.GetValue<string>()).Should().Equal("title 1", "title 4", null, null);
        }
    }
}
