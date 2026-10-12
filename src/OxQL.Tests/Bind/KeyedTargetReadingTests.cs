using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A path under an alias joined after the page is read on its target as the request's contract reads
/// the target itself: a member no row can carry is refused under contract 2.
/// </summary>
public class KeyedTargetReadingTests
{
    private const string Note = "kx.note";
    private const string Author = "kx.author";

    private static readonly EntityModel Model = ClrModelBuilder.Build(
    [
        new EntityDeclaration(Note, Note, typeof(NoteModel), "tmp_kx_notes", null, false),
        new EntityDeclaration(Author, Author, typeof(AuthorModel), "tmp_kx_authors", null, false),
    ]);

    public sealed class NoteModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference(Author)]
        public List<Guid> AuthorIds { get; set; } = [];
    }

    public sealed class AuthorModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public string Label => Name + "!";
    }

    private const string Joined = """{ "resolve": { "path": "authorIds", "as": "a", "elements": "all" } }""";

    [Fact]
    public async Task A_member_no_row_can_carry_is_refused_under_a_keyed_alias_as_it_is_on_its_target()
    {
        var joined = await BindHost.BoundAsync(Model, Note, $$"""[{{Joined}}, { "project": { "a.name": 1 } }]""");

        joined.FinalShape.Roots["a"].Should().BeOfType<ShapeNode.Keyed>();

        (await BindHost.ErrorAsync(Model, Author, """[{ "project": { "label": 1 } }]""", Codes.NotStored)).Path.Should().Be("label");
        (await BindHost.ErrorAsync(Model, Note, $$"""[{{Joined}}, { "project": { "a.label": 1 } }]""", Codes.NotStored)).Path.Should().Be("a.label");
    }
}
