using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The comparisons the engine writes as a pattern (the text operators, and an exact comparison
/// beside one that folds) over values that hold line feeds: a pattern built from a text matches the
/// values the same comparison written as a plain operator matches. The end of the text is the end
/// of the value, not the place before a line feed that ends it; the start is the start of the
/// value, not of a line.
/// </summary>
[Trait("Category", "Integration")]
public class PatternTextServerTests
{
    private const string Entity = "pt.note";
    private const string Collection = "tmp_pt_notes";

    public sealed class NoteModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Text { get; set; } = "";

        public string? Other { get; set; }
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(NoteModel), Collection, null, false)]));

    /// <summary>The values, by the name a case expects them under.</summary>
    private static readonly (string Name, string Text)[] Values =
    [
        ("one", "1"), ("one-lf", "1\n"), ("lf-one", "\n1"), ("one-crlf", "1\r\n"), ("a-lf-one", "a\n1"), ("one-lf-a", "1\na"), ("ONE", "ONE"), ("one-word", "one"), ("one-word-lf", "one\n"),
    ];

    private static async Task<(TestDatabase Owned, IQueryEngine Engine)> SeedAsync(string purpose, IEnumerable<(string Name, string Text)> values)
    {
        var owned = await MongoFixture.CreateDatabaseAsync(purpose);

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(values.Select(value => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["Text"] = value.Text,
            ["Other"] = value.Name,
        }));

        return (owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name));
    }

    private static async Task<List<string>> NamesAsync(IQueryEngine engine, string condition, string? variables = null)
    {
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, $$"""[{ "match": {{condition}} }, { "project": { "other": 1 } }, { "page": { "limit": 100 } }]""", variables), Direct.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>($"{condition}: {(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "")}");

        return ((QueryOutcome.Success)outcome).Result.Items.Select(row => row!["other"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
    }

    private static string[] Names(params string[] names) => [.. names.Order(StringComparer.Ordinal)];

    /// <summary>A condition that folds, so the request runs under the collation and an exact comparison beside it is a pattern.</summary>
    private const string Folding = """ "other": { "neq": "nobody" } """;

    [Fact]
    public async Task The_end_of_a_text_is_the_end_of_the_value()
    {
        var (owned, engine) = await SeedAsync("pattern_end", Values);

        await using (owned)
        {
            // endsWith, folding and exact.
            (await NamesAsync(engine, """{ "text": { "endsWith": "1" } }""")).Should().Equal(Names("one", "lf-one", "a-lf-one"));
            (await NamesAsync(engine, """{ "text": { "endsWith": "1", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("one", "lf-one", "a-lf-one"));
            (await NamesAsync(engine, """{ "text": { "endsWith": "E" } }""")).Should().Equal(Names("ONE", "one-word"));
            (await NamesAsync(engine, """{ "text": { "endsWith": "1\n" } }""")).Should().Equal(Names("one-lf"));

            // An exact comparison beside a folding one is the comparison it is alone.
            foreach (var (op, operand) in new[] { ("eq", "\"1\""), ("neq", "\"1\""), ("in", "[\"1\", \"one\"]"), ("nin", "[\"1\", \"one\"]"), ("eq", "\"one\\n\""), ("neq", "\"1\\n\"") })
            {
                var exact = $$"""{ "{{op}}": {{operand}}, "options": { "caseSensitive": true } }""";
                var alone = await NamesAsync(engine, $$"""{ "text": {{exact}} }""");
                var beside = await NamesAsync(engine, $$"""{ "text": {{exact}}, {{Folding}} }""");

                beside.Should().Equal(alone, $"{op} {operand}");
            }

            (await NamesAsync(engine, $$"""{ "text": { "eq": "1", "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names("one"));
            (await NamesAsync(engine, $$"""{ "text": { "in": ["one", "1\n"], "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names("one-word", "one-lf"));
            (await NamesAsync(engine, $$"""{ "text": { "nin": ["1"], "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names([.. Values.Select(value => value.Name).Where(name => name != "one")]));
        }
    }

    [Fact]
    public async Task The_start_of_a_text_is_the_start_of_the_value_and_not_of_a_line()
    {
        var (owned, engine) = await SeedAsync("pattern_start", Values);

        await using (owned)
        {
            (await NamesAsync(engine, """{ "text": { "startsWith": "1" } }""")).Should().Equal(Names("one", "one-lf", "one-crlf", "one-lf-a"));
            (await NamesAsync(engine, """{ "text": { "startsWith": "1", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("one", "one-lf", "one-crlf", "one-lf-a"));
            (await NamesAsync(engine, """{ "text": { "startsWith": "a", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("a-lf-one"));
            (await NamesAsync(engine, """{ "text": { "contains": "\n" } }""")).Should().Equal(Names("one-lf", "lf-one", "one-crlf", "a-lf-one", "one-lf-a", "one-word-lf"));
        }
    }

    /// <summary>
    /// A NUL is a character of a text like any other: a pattern built from a text that holds one
    /// finds the values a plain comparison finds, and a caller's own pattern that holds one is
    /// refused as the pattern it is, since a pattern is sent as text that ends at its first NUL.
    /// </summary>
    [Fact]
    public async Task A_text_that_holds_a_NUL_is_matched_as_a_plain_comparison_matches_it()
    {
        var (owned, engine) = await SeedAsync("pattern_nul", [("nul", "x\0y"), ("plain", "xy"), ("upper", "X\0Y"), ("x", "x")]);

        await using (owned)
        {
            (await NamesAsync(engine, """{ "text": { "eq": "x\u0000y", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("nul"));
            (await NamesAsync(engine, $$"""{ "text": { "eq": "x\u0000y", "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names("nul"));
            (await NamesAsync(engine, $$"""{ "text": { "in": ["x\u0000y", "x"], "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names("nul", "x"));
            (await NamesAsync(engine, $$"""{ "text": { "neq": "x\u0000y", "options": { "caseSensitive": true } }, {{Folding}} }""")).Should().Equal(Names("plain", "upper", "x"));
            (await NamesAsync(engine, """{ "text": { "contains": "\u0000" } }""")).Should().Equal(Names("nul", "upper"));
            (await NamesAsync(engine, """{ "text": { "contains": "x\u0000", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("nul"));
            (await NamesAsync(engine, """{ "text": { "endsWith": "\u0000Y" } }""")).Should().Equal(Names("nul", "upper"));
            (await NamesAsync(engine, """{ "text": { "endsWith": "\u0000y", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("nul"));
            (await NamesAsync(engine, """{ "text": { "startsWith": "x\u0000", "options": { "caseSensitive": true } } }""")).Should().Equal(Names("nul"));
            (await NamesAsync(engine, """{ "text": { "contains": { "$var": "part" } } }""", """{ "part": "\u0000y" }""")).Should().Equal(Names("nul", "upper"));
            (await NamesAsync(engine, """{ "text": { "regex": "^x\\x00y$" } }""")).Should().Equal(Names("nul"));

            var refused = await engine.ExecuteAsync(EngineDirect.Request(Entity, """[{ "match": { "text": { "regex": "^x\u0000" } } }]"""), Direct.Context());

            refused.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.InvalidRegex);
        }
    }
}
