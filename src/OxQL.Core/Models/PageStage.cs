using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>The page stage: once, last. A cursor continues a page; an offset jumps, under the host cap.</summary>
[JsonConverter(typeof(PageStageConverter))]
public sealed record PageStage
{
    /// <summary>The page size; the host's default when absent.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>The opaque cursor of the page to continue from.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    /// <summary>The number of rows to skip, under <c>maxOffset</c>.</summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    /// <summary>Whether to count the matching rows, up to the count cap.</summary>
    [JsonPropertyName("includeTotalCount")]
    public bool IncludeTotalCount { get; init; }

    /// <summary>
    /// Member names the caller wrote that the stage does not have — <c>skip</c> for
    /// <c>offset</c> among them. The binder refuses them: a dropped member would take the page
    /// from the top.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

internal sealed class PageStageConverter : JsonConverter<PageStage>
{
    public override PageStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var stage = new PageStage();
        var unknown = new List<string>();

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return stage;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "limit": stage = stage with { Limit = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : null }; break;
                case "cursor": stage = stage with { Cursor = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null }; break;
                case "offset": stage = stage with { Offset = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : null }; break;
                case "includeTotalCount": stage = stage with { IncludeTotalCount = property.Value.ValueKind == JsonValueKind.True }; break;
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown };
    }

    public override void Write(Utf8JsonWriter writer, PageStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Limit is not null) writer.WriteNumber("limit", value.Limit.Value);
        if (value.Cursor is not null) writer.WriteString("cursor", value.Cursor);
        if (value.Offset is not null) writer.WriteNumber("offset", value.Offset.Value);
        if (value.IncludeTotalCount) writer.WriteBoolean("includeTotalCount", true);
        writer.WriteEndObject();
    }
}
