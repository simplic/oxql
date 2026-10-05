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

    /// <summary>Whether the caller wrote <c>offset</c>, whatever its value: the public contract 2 route does not have the member.</summary>
    [JsonIgnore]
    public bool OffsetWritten { get; init; }

    /// <summary>Whether to count the matching rows, up to the count cap. True as well when the member is a number.</summary>
    [JsonPropertyName("includeTotalCount")]
    public bool IncludeTotalCount { get; init; }

    /// <summary>
    /// The cap the caller wrote as the number form of <c>includeTotalCount</c>: the count stops
    /// there instead of at the host's <c>CountCap</c>, which still bounds it. Null for the
    /// boolean form. A number that is not a positive integer is 0 here, and the binder refuses it.
    /// </summary>
    [JsonIgnore]
    public int? TotalCountCap { get; init; }

    /// <summary>Whether the caller wrote <c>includeTotalCount</c>, whatever its value: the public contract 2 route does not have the member.</summary>
    [JsonIgnore]
    public bool IncludeTotalCountWritten { get; init; }

    /// <summary>
    /// The contract 2 count request (<c>count</c>): <c>true</c>, <c>false</c>, or an object that bounds
    /// it (<c>upTo</c>, <c>budgetMs</c>). Null when the member was not written.
    /// </summary>
    [JsonIgnore]
    public PageCount? Count { get; init; }

    /// <summary>
    /// Member names the caller wrote that the stage does not have — <c>skip</c> for
    /// <c>offset</c> among them. The binder refuses them: a dropped member would take the page
    /// from the top.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

/// <summary>
/// The count a contract 2 page asks for (DG2 M3): whether one is asked, and the bounds the caller
/// put on it. <see cref="UpTo"/> is the most rows the caller needs counted; <see cref="BudgetMs"/>
/// the time it gives the count. A member of the object form that is not a whole number is 0
/// here, and the binder refuses it.
/// </summary>
public sealed record PageCount
{
    /// <summary>Whether a count is asked: <c>true</c> or the object form.</summary>
    public bool Asked { get; init; }

    /// <summary>The most rows the caller needs counted, under the host's <c>CountCap</c>; null when not written.</summary>
    public int? UpTo { get; init; }

    /// <summary>The time the caller gives the count, in milliseconds, under the host's <c>Count:MaxBudgetMs</c>; null when not written.</summary>
    public int? BudgetMs { get; init; }

    /// <summary>Whether the member was written as something other than <c>true</c>, <c>false</c> or an object; the binder refuses it.</summary>
    public bool Invalid { get; init; }

    /// <summary>The members of the object form the engine does not know; the binder refuses them.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    internal static PageCount Read(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: return new PageCount { Asked = true };
            case JsonValueKind.False: return new PageCount();
            case JsonValueKind.Object: break;
            default: return new PageCount { Invalid = true };
        }

        var count = new PageCount { Asked = true };
        var unknown = new List<string>();

        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "upTo": count = count with { UpTo = Whole(property.Value) }; break;
                case "budgetMs": count = count with { BudgetMs = Whole(property.Value) }; break;
                default: unknown.Add(property.Name); break;
            }
        }

        return count with { Unknown = unknown };

        // A number above the widest int is above every host bound and clamps to it; anything that is not a whole number is 0.
        static int Whole(JsonElement number) =>
            number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var whole) ? (int)Math.Clamp(whole, int.MinValue, int.MaxValue) : 0;
    }

    internal void Write(Utf8JsonWriter writer)
    {
        if (!Asked)
        {
            writer.WriteBoolean("count", false);
            return;
        }

        if (UpTo is null && BudgetMs is null)
        {
            writer.WriteBoolean("count", true);
            return;
        }

        writer.WriteStartObject("count");
        if (UpTo is { } upTo) writer.WriteNumber("upTo", upTo);
        if (BudgetMs is { } budget) writer.WriteNumber("budgetMs", budget);
        writer.WriteEndObject();
    }
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
                case "offset": stage = stage with { Offset = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : null, OffsetWritten = true }; break;
                case "count": stage = stage with { Count = PageCount.Read(property.Value) }; break;
                case "includeTotalCount":
                    stage = stage with { IncludeTotalCountWritten = true };
                    stage = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => stage with { IncludeTotalCount = true },
                        // A cap above the widest int is above every host cap and clamps to it;
                        // a fraction is no cap at all and is left at 0 for the binder to refuse.
                        JsonValueKind.Number => stage with { IncludeTotalCount = true, TotalCountCap = property.Value.TryGetInt64(out var cap) ? (int)Math.Clamp(cap, int.MinValue, int.MaxValue) : 0 },
                        _ => stage with { IncludeTotalCount = false },
                    };
                    break;
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
        if (value.TotalCountCap is { } cap) writer.WriteNumber("includeTotalCount", cap);
        else if (value.IncludeTotalCount) writer.WriteBoolean("includeTotalCount", true);
        value.Count?.Write(writer);
        writer.WriteEndObject();
    }
}
