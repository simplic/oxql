using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.Model;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The Ox Schema document (format 1.1) a fleet service would publish on <c>GET /schema</c>, for the
/// studio's fixtures: the fleet's hosts run the engine without the base package, so nothing serves
/// the route, and this writes what the base package's builder (<c>OxSchemaBuilder</c>,
/// <c>TypePoolWalker</c>) makes of the same model, member for member and in its property order:
/// the type pool with every member's kind, pointer, storage name, references and reference cases,
/// variants and <c>onlyFor</c>; each entity with its label, key, display, retired ids, extendable,
/// queryable, not-filterable paths and item collections; the limits; and the revision over the
/// canonical form. What only a service has is left out: controller operations, legacy item aliases,
/// and a non-entity type's key comes from its stored <c>_id</c> member, since the fleet's types do
/// not implement the base package's identity interfaces.
/// </summary>
internal static class FleetSchemaDocument
{
    private static readonly string[] DisplayCandidates = ["name", "matchCode", "number"];
    private static readonly string[] LabelSuffixes = ["Model", "Response", "Dto"];

    private static readonly JsonSerializerOptions Canonical = new()
    {
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
    };

    public static JsonObject Of(LabService service, OxQLOptions? options = null)
    {
        var model = service.Model;
        var limits = (options ?? new OxQLOptions()).Limits;
        var types = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var (id, type) in model.TypePool)
            types[id] = type.IsEnum ? Enum(type) : Type(type);

        foreach (var entity in model.Entities.Values.Where(entity => entity.ClrType is not null))
        {
            var entry = types[entity.Id];
            var properties = entry["properties"] as JsonArray ?? [];

            types[entity.Id] = Ordered(entry, new JsonObject
            {
                ["displayName"] = Label(entity.ClrType!.Name),
                ["entity"] = true,
                ["aliases"] = new JsonArray([.. entity.RetiredIds.Order(StringComparer.Ordinal).Select(alias => (JsonNode?)alias)]),
                ["key"] = entity.Key is { } key ? new JsonArray(key.Wire) : null,
                ["display"] = DisplayOf(properties),
                ["extendable"] = entity.Extendable,
                ["queryable"] = true,
                ["notFilterable"] = new JsonArray([.. entity.Paths.Where(path => !path.Stored && Kinds.IsScalar(path.LeafKind)).Select(path => path.Wire).Order(StringComparer.Ordinal).Select(path => (JsonNode?)path)]),
                ["notSortable"] = new JsonArray(),
            });
        }

        foreach (var entity in model.Entities.Values.Where(entity => entity.ClrType is not null))
        {
            var items = new List<string>();

            Collect(types, entity.Id, "", [entity.Id], items);
            types[entity.Id] = Ordered(types[entity.Id], new JsonObject
            {
                ["items"] = new JsonArray([.. items.Select(path => (JsonNode?)new JsonObject { ["path"] = path, ["aliases"] = new JsonArray() })]),
            });
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = "1.1",
            ["service"] = service.Key,
            ["api"] = new JsonObject { ["name"] = service.Key + "-api", ["version"] = "v1" },
            ["revision"] = null,
            ["limits"] = new JsonObject
            {
                ["maxPageSize"] = limits.MaxPageSize,
                ["defaultPageSize"] = limits.DefaultPageSize,
                ["maxPipelineStages"] = limits.MaxPipelineStages,
                ["maxLookupStages"] = limits.MaxLookupStages,
                ["maxUnwindStages"] = limits.MaxUnwindStages,
                ["maxGroupFields"] = limits.MaxGroupFields,
                ["maxProjectionFields"] = limits.MaxProjectionFields,
                ["regexMaxLength"] = limits.RegexMaxLength,
                ["maxOffset"] = limits.MaxOffset,
                ["maxResolveStages"] = limits.MaxResolveStages,
                ["maxBatchQueries"] = limits.MaxBatchQueries,
                ["maxLookupLimit"] = limits.MaxLookupLimit,
                ["maxContinuedStages"] = limits.MaxContinuedStages,
                ["maxFlattenDepth"] = limits.MaxFlattenDepth,
                ["maxReportPageSize"] = limits.MaxReportPageSize,
            },
            ["types"] = new JsonObject(types.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
        };

        var canonical = WithoutNulls(document)!.AsObject();

        canonical.Remove("revision");

        var revision = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString(Canonical))));
        var published = WithoutNulls(document)!.AsObject();

        published["revision"] = revision;

        return Ordered(published, [], ["schemaVersion", "service", "api", "revision", "limits", "types"]);
    }

    private static JsonObject Type(TypeDef type)
    {
        var properties = new JsonArray([.. type.Members.Select(member => (JsonNode?)Property(member))]);

        return new JsonObject
        {
            ["description"] = type.Description,
            ["key"] = !type.IsEntity && type.Members.Any(member => member.StorageName == "_id") ? new JsonArray(type.Members.First(member => member.StorageName == "_id").WireName) : null,
            ["properties"] = properties,
            ["discriminator"] = type.Variants.Count == 0 ? null : new JsonObject
            {
                ["element"] = type.DiscriminatorElement ?? "_t",
                ["form"] = type.DiscriminatorForm == DiscriminatorForm.Hierarchical ? "hierarchical" : "scalar",
            },
            ["variants"] = type.Variants.Count == 0 ? null : new JsonArray([.. type.Variants.OrderBy(variant => variant.Name, StringComparer.Ordinal)
                .Select(variant => (JsonNode?)new JsonObject { ["name"] = variant.Name, ["type"] = "#/types/" + variant.Type.PoolId })]),
        };
    }

    private static JsonObject Enum(TypeDef type) => new()
    {
        ["kind"] = "enum",
        ["description"] = type.Description,
        ["flags"] = type.EnumFlags,
        ["values"] = new JsonArray([.. type.EnumValues.Select(value => (JsonNode?)new JsonObject
        {
            ["name"] = value.Name, ["value"] = value.Value, ["active"] = value.Active, ["description"] = value.Description,
        })]),
    };

    private static JsonObject Property(MemberDef member)
    {
        var shape = Shape(member);
        var clrName = member.ClrName ?? Pascalize(member.WireName);

        return new JsonObject
        {
            ["name"] = member.WireName,
            ["storageName"] = clrName == Pascalize(member.WireName) ? null : clrName,
            ["kind"] = shape["kind"]!.DeepClone(),
            ["type"] = shape["type"]?.DeepClone(),
            ["of"] = shape["of"]?.DeepClone(),
            ["value"] = shape["value"]?.DeepClone(),
            ["nullable"] = member.Nullable,
            ["displayName"] = member.DisplayName,
            ["description"] = member.Description,
            ["snapshotOf"] = shape["snapshotOf"]?.DeepClone(),
            ["references"] = member.References is [{ IsSimple: true } simple]
                ? new JsonObject { ["entity"] = simple.TargetEntity, ["field"] = simple.TargetField, ["joinable"] = true, ["inferred"] = false }
                : null,
            ["constraints"] = member.Constraints is { } constraints
                ? new JsonObject { ["maxLength"] = constraints.MaxLength, ["min"] = constraints.Min, ["max"] = constraints.Max, ["pattern"] = constraints.Pattern }
                : null,
            ["deprecated"] = member.Deprecated is { } deprecated
                ? new JsonObject { ["since"] = deprecated.Since, ["replacedBy"] = deprecated.ReplacedBy, ["note"] = deprecated.Note }
                : null,
            ["onlyFor"] = member.OnlyFor is { Count: > 0 } onlyFor ? new JsonArray([.. onlyFor.Select(name => (JsonNode?)name)]) : null,
            ["referenceCases"] = member.References.Count == 0 || member.References is [{ IsSimple: true }]
                ? null
                : new JsonArray([.. member.References.Select(declared => (JsonNode?)new JsonObject
                {
                    ["when"] = declared.When switch
                    {
                        ReferenceCondition.PathEquals equals => new JsonObject { ["path"] = equals.Path, ["equals"] = new JsonArray([.. equals.Values.Select(value => (JsonNode?)value)]) },
                        ReferenceCondition.Variant variant => new JsonObject { ["variant"] = new JsonArray([.. variant.Names.Select(name => (JsonNode?)name)]) },
                        _ => null,
                    },
                    ["keyAs"] = declared.KeyAs == KeyAs.Guid ? "guid" : null,
                    ["targets"] = new JsonArray([.. declared.Targets.Select(target => (JsonNode?)new JsonObject { ["entity"] = target.Entity, ["item"] = target.Item, ["field"] = target.Field })]),
                })]),
        };
    }

    private static JsonObject Shape(ShapeDef shape) => shape.Kind switch
    {
        Kind.Enum => new JsonObject { ["kind"] = "enum", ["type"] = Pointer(shape) },
        Kind.Dictionary => new JsonObject { ["kind"] = "dictionary", ["value"] = shape.Value is null ? null : Shape(shape.Value) },
        Kind.Array => new JsonObject { ["kind"] = "array", ["of"] = shape.Of is null ? null : Shape(shape.Of) },
        Kind.Object => new JsonObject { ["kind"] = "object", ["type"] = Pointer(shape), ["snapshotOf"] = shape.SnapshotOf },
        var kind => new JsonObject { ["kind"] = Kinds.NameOf(kind) },
    };

    private static string? Pointer(ShapeDef shape) => shape.Type is null ? null : "#/types/" + shape.Type.PoolId;

    /// <summary>The paths of an entity's collections of keyed objects, as the base package's item collections walk them.</summary>
    private static void Collect(SortedDictionary<string, JsonObject> types, string typeId, string prefix, List<string> stack, List<string> paths)
    {
        if (!types.TryGetValue(typeId, out var entry))
            return;

        foreach (var property in (entry["properties"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (property["name"]?.GetValue<string>() is not { } name)
                continue;

            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            var element = property["kind"]?.GetValue<string>() == "array" ? property["of"] as JsonObject : null;
            var target = element is not null
                ? element["kind"]?.GetValue<string>() == "object" ? element["type"]?.GetValue<string>() : null
                : property["kind"]?.GetValue<string>() == "object" ? property["type"]?.GetValue<string>() : null;

            if (target is null)
                continue;

            var id = target["#/types/".Length..];

            if (!types.TryGetValue(id, out var pointee) || pointee["entity"]?.GetValue<bool>() == true)
                continue;

            if (element is not null && pointee["key"] is JsonArray { Count: > 0 })
                paths.Add(path);

            if (stack.Contains(id, StringComparer.Ordinal))
                continue;

            stack.Add(id);
            Collect(types, id, path, stack, paths);
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static string? DisplayOf(JsonArray properties)
    {
        foreach (var candidate in DisplayCandidates)
            if (properties.OfType<JsonObject>().Any(property => property["name"]?.GetValue<string>() == candidate && property["kind"]?.GetValue<string>() == "string"))
                return candidate;

        return null;
    }

    private static string Label(string name)
    {
        foreach (var suffix in LabelSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }

        var label = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (index > 0 && char.IsUpper(character) && (!char.IsUpper(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                label.Append(' ');

            label.Append(character);
        }

        return label.ToString();
    }

    private static string Pascalize(string wire) => wire.Length == 0 ? wire : char.ToUpperInvariant(wire[0]) + wire[1..];

    /// <summary>The members of a type entry in the base package's order: kind … variants.</summary>
    private static readonly string[] TypeOrder =
        ["kind", "displayName", "description", "flags", "values", "entity", "aliases", "key", "display", "extendable", "queryable", "notFilterable", "notSortable", "operations", "items", "properties", "discriminator", "variants"];

    private static JsonObject Ordered(JsonObject entry, JsonObject set, IReadOnlyList<string>? order = null)
    {
        var merged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var (name, value) in entry)
            merged[name] = value?.DeepClone();

        foreach (var (name, value) in set)
            merged[name] = value?.DeepClone();

        var result = new JsonObject();

        foreach (var name in (order ?? TypeOrder).Concat(merged.Keys.Where(key => !(order ?? TypeOrder).Contains(key))))
            if (merged.TryGetValue(name, out var value) && value is not null)
                result[name] = value;

        return result;
    }

    /// <summary>The node without null members at any depth, as the base package's serializer writes it.</summary>
    private static JsonNode? WithoutNulls(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(pair => pair.Value is not null).Select(pair => KeyValuePair.Create(pair.Key, WithoutNulls(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(WithoutNulls)]),
        null => null,
        _ => node.DeepClone(),
    };
}
