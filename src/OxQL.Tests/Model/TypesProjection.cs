using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.Tests.Model;

/// <summary>
/// Renders a model's wire view as the schema document's <c>types</c> section, so a model can
/// be compared with the document it was built from, or with the document a CLR graph would
/// publish. The members the model does not carry (operations, item collections, legacy
/// aliases, inferred references, item keys) are stripped from a source document by
/// <see cref="Comparable"/> before the comparison.
/// </summary>
internal static class TypesProjection
{
    public static JsonObject Project(EntityModel model)
    {
        var types = new JsonObject();

        foreach (var (id, type) in model.TypePool)
            types[id] = type.IsEnum ? ProjectEnum(type) : ProjectObject(model, type);

        return types;
    }

    /// <summary>A source document's <c>types</c> reduced to what the model carries.</summary>
    public static JsonObject Comparable(JsonElement document)
    {
        var types = JsonNode.Parse(document.GetProperty("types").GetRawText())!.AsObject();

        foreach (var (_, entry) in types)
        {
            var type = entry!.AsObject();
            var isEntity = type["entity"]?.GetValue<bool>() == true;

            type.Remove("operations");
            type.Remove("items");
            type.Remove("description");

            if (!isEntity)
                type.Remove("key");

            if (type["aliases"] is JsonArray aliases)
                for (var index = aliases.Count - 1; index >= 0; index--)
                    if (aliases[index]!.GetValue<string>().StartsWith('$'))
                        aliases.RemoveAt(index);

            if (type["properties"] is JsonArray properties)
                foreach (var property in properties)
                    Reduce(property!.AsObject());
        }

        return types;
    }

    private static void Reduce(JsonObject descriptor)
    {
        descriptor.Remove("description");
        descriptor.Remove("constraints");
        descriptor.Remove("deprecated");

        if (descriptor["references"] is JsonObject reference && reference["inferred"]?.GetValue<bool>() == true)
            descriptor.Remove("references");

        if (descriptor["of"] is JsonObject of)
            Reduce(of);

        if (descriptor["value"] is JsonObject value)
            Reduce(value);
    }

    private static JsonObject ProjectEnum(TypeDef type) => new()
    {
        ["kind"] = "enum",
        ["flags"] = type.EnumFlags,
        ["values"] = new JsonArray(type.EnumValues.Select(value => (JsonNode)new JsonObject
        {
            ["name"] = value.Name,
            ["value"] = value.Value,
            ["active"] = value.Active,
        }).ToArray()),
    };

    private static JsonObject ProjectObject(EntityModel model, TypeDef type)
    {
        var entry = new JsonObject();

        if (type.IsEntity)
        {
            var entity = model.Entities[type.PoolId];

            if (entity.DisplayName is not null)
                entry["displayName"] = entity.DisplayName;

            entry["entity"] = true;
            entry["aliases"] = new JsonArray(entity.RetiredIds.Select(id => (JsonNode)id).ToArray());

            if (entity.Key is not null)
                entry["key"] = new JsonArray(entity.Key.Wire);

            if (entity.Display is not null)
                entry["display"] = entity.Display.Wire;

            entry["extendable"] = entity.Extendable;
            entry["queryable"] = true;
            entry["notFilterable"] = new JsonArray();
            entry["notSortable"] = new JsonArray();
        }

        entry["properties"] = new JsonArray(type.Members.Select(member => (JsonNode)ProjectMember(member)).ToArray());

        return entry;
    }

    private static JsonObject ProjectMember(MemberDef member)
    {
        var descriptor = new JsonObject { ["name"] = member.WireName };

        if (member.Stored && member.StorageName != WireNames.DerivedStorage(member.WireName))
            descriptor["storageName"] = member.StorageName;

        ProjectShape(descriptor, member);

        descriptor["nullable"] = member.Nullable;

        if (member.DisplayName is not null)
            descriptor["displayName"] = member.DisplayName;

        if (member.Reference is { } reference)
            descriptor["references"] = new JsonObject
            {
                ["entity"] = reference.TargetEntity,
                ["field"] = reference.TargetField,
                ["joinable"] = false,
                ["inferred"] = false,
            };

        return descriptor;
    }

    private static JsonObject ProjectShape(JsonObject descriptor, ShapeDef shape)
    {
        descriptor["kind"] = Kinds.NameOf(shape.Kind);

        if (shape.Type is not null)
            descriptor["type"] = "#/types/" + shape.Type.PoolId;

        if (shape.Of is not null)
            descriptor["of"] = ProjectShape(new JsonObject(), shape.Of);

        if (shape.Value is not null)
            descriptor["value"] = ProjectShape(new JsonObject(), shape.Value);

        if (shape.SnapshotOf is not null)
            descriptor["snapshotOf"] = shape.SnapshotOf;

        return descriptor;
    }

    public static string Pretty(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
