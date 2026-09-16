using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Model;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// The two builders describe one graph: the CLR fixture assembly and the document written
/// for it. They agree on every path except where only the registry knows the truth, and those
/// divergences are named here, one test per class.
/// </summary>
public class EquivalenceTests
{
    private static readonly EntityModel Clr = ProbeModel.Clr;
    private static readonly EntityModel Document = ProbeModel.Document();

    [Fact]
    public void Both_builders_describe_the_same_entities_under_the_same_pool_ids()
    {
        Document.Entities.Keys.Should().Equal(Clr.Entities.Keys);
        Document.TypePool.Keys.Except(Clr.TypePool.Keys).Should().Equal("t_geoPoint");
        Clr.TypePool.Keys.Except(Document.TypePool.Keys).Should().BeEmpty();

        foreach (var (id, entity) in Clr.Entities)
        {
            var other = Document.Entities[id];

            other.Collection.Should().Be(entity.Collection, id);
            other.Database.Should().Be(entity.Database, id);
            other.Extendable.Should().Be(entity.Extendable, id);
            other.DisplayName.Should().Be(entity.DisplayName, id);
            other.Key?.Wire.Should().Be(entity.Key?.Wire, id);
            other.Display?.Wire.Should().Be(entity.Display?.Wire, id);
            other.RetiredIds.Should().Equal(entity.RetiredIds, id);
        }

        Document.RetiredIds.Should().BeEquivalentTo(Clr.RetiredIds);
    }

    [Fact]
    public void Path_indexes_are_identical_except_the_member_the_registry_cannot_describe()
    {
        foreach (var (id, entity) in Clr.Entities)
        {
            var (onlyClr, onlyDocument) = PathSnapshot.Diff(entity, Document.Entities[id]);

            if (id != "probe.order")
            {
                onlyClr.Should().BeEmpty(id);
                onlyDocument.Should().BeEmpty(id);
                continue;
            }

            // The opaque serializer: the registry says unknown, the document describes the CLR
            // shape underneath, which does not exist in storage (the GeoJsonPoint case).
            onlyClr.Select(path => path.Wire).Should().Equal("location");
            onlyClr[0].Kind.Should().Be("unknown");
            onlyDocument.Select(path => path.Wire).Should().Equal("location", "location.lat", "location.lng");
            onlyDocument[0].Kind.Should().Be("object");
        }
    }

    [Fact]
    public void Without_the_registry_overrides_the_document_is_wrong_about_exactly_the_measured_divergences()
    {
        var naive = OxQL.Model.Build.DocumentModelBuilder.Build(ProbeModel.ReadFixture("probe.json"), new OxQL.Model.Build.DocumentModelOptions
        {
            Storage = ProbeModel.Storage,
        });

        var (onlyClr, onlyDocument) = PathSnapshot.Diff(Clr.Entities["probe.order"], naive.Entities["probe.order"]);

        // [BsonIgnore], get-only (concrete and abstract), [BsonElement], an ArrayOfDocuments dictionary, the opaque serializer.
        onlyClr.Select(path => path.Wire).Should().BeEquivalentTo(
            "hidden", "computed", "figure.figureKind", "renamed", "priceList.*", "priceList.*.net", "priceList.*.currency", "location");
        onlyDocument.Select(path => path.Wire).Should().BeEquivalentTo(
            "hidden", "computed", "figure.figureKind", "renamed", "priceList.*", "priceList.*.net", "priceList.*.currency", "location", "location.lat", "location.lng");

        onlyDocument.Single(path => path.Wire == "hidden").Storage.Should().Be("Hidden", "the document derives a storage name that matches no rows");
        onlyClr.Single(path => path.Wire == "hidden").Storage.Should().BeNull();
        onlyDocument.Single(path => path.Wire == "renamed").Storage.Should().Be("Renamed");
        onlyClr.Single(path => path.Wire == "renamed").Storage.Should().Be("x");
    }

    [Fact]
    public void The_clr_model_projects_to_the_document_the_schema_would_publish()
    {
        using var document = ProbeModel.ParseFixture("probe.json");
        var expected = TypesProjection.Comparable(document.RootElement);
        var projected = TypesProjection.Project(Clr);

        // The schema does not know the rename and describes the opaque type's CLR shape.
        var order = projected["probe.order"]!["properties"]!.AsArray();

        order.First(property => property!["name"]!.GetValue<string>() == "renamed")!.AsObject().Remove("storageName");

        var location = order.First(property => property!["name"]!.GetValue<string>() == "location")!.AsObject();

        location["kind"] = "object";
        location["type"] = "#/types/t_geoPoint";
        projected["t_geoPoint"] = JsonNode.Parse(expected["t_geoPoint"]!.ToJsonString());

        JsonNode.DeepEquals(projected, expected).Should().BeTrue(
            "expected:\n{0}\nprojected:\n{1}", TypesProjection.Pretty(expected), TypesProjection.Pretty(projected));
    }

    [Fact]
    public void Both_builders_describe_the_same_enum_entries()
    {
        foreach (var (id, type) in Clr.TypePool.Where(pair => pair.Value.IsEnum))
        {
            var other = Document.TypePool[id];

            other.IsEnum.Should().BeTrue(id);
            other.EnumFlags.Should().Be(type.EnumFlags, id);
            other.EnumValues.Should().Equal(type.EnumValues, id);
        }
    }
}
