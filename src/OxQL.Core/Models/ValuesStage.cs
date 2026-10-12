using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// The values stage (contract 2): the distinct values of one scalar path among the rows that reach
/// it, in place of the page. Once, last. It answers one row per value (<c>value</c>, and
/// <c>count</c> with <see cref="Counts"/>), cut to <see cref="Limit"/>; the rows it reads are bounded
/// by <see cref="Scan"/>.
/// </summary>
[JsonConverter(typeof(ValuesStageConverter))]
public sealed record ValuesStage
{
    /// <summary>The wire path whose values are asked for: a scalar, also one under collections that are not unwound.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The text a value starts with, folded as the path's comparisons fold; null for every value.</summary>
    [JsonPropertyName("search")]
    public string? Search { get; init; }

    /// <summary>The most values answered; the host's default when absent.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>Whether each value carries the number of rows that hold it; they then lead by that number.</summary>
    [JsonPropertyName("counts")]
    public bool Counts { get; init; }

    /// <summary>The most rows read for the values; the host's bound when absent.</summary>
    [JsonPropertyName("scan")]
    public int? Scan { get; init; }

    /// <summary>The members the caller wrote with a value of another JSON kind than the member takes; the binder refuses them.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Invalid { get; init; } = [];

    /// <summary>Member names the caller wrote that the stage does not have.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

internal sealed class ValuesStageConverter : JsonConverter<ValuesStage>
{
    public override ValuesStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var stage = new ValuesStage();
        var unknown = new List<string>();
        var invalid = new List<string>();

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return stage with { Invalid = ["values"] };

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var value = property.Value;

            switch (property.Name)
            {
                case "path" when value.ValueKind == JsonValueKind.String: stage = stage with { Path = value.GetString() }; break;
                case "search" when value.ValueKind == JsonValueKind.String: stage = stage with { Search = value.GetString() }; break;
                case "search" when value.ValueKind == JsonValueKind.Null: break;
                case "limit" when Whole(value) is { } limit: stage = stage with { Limit = limit }; break;
                case "scan" when Whole(value) is { } scan: stage = stage with { Scan = scan }; break;
                case "counts" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: stage = stage with { Counts = value.GetBoolean() }; break;
                case "path" or "search" or "limit" or "scan" or "counts": invalid.Add(property.Name); break;
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown, Invalid = invalid };

        // A number above the widest int is above every host bound and clamps to it; anything that is not a whole number is no value.
        static int? Whole(JsonElement number) =>
            number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var whole) ? (int)Math.Clamp(whole, int.MinValue, int.MaxValue) : null;
    }

    public override void Write(Utf8JsonWriter writer, ValuesStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Path is not null) writer.WriteString("path", value.Path);
        if (value.Search is not null) writer.WriteString("search", value.Search);
        if (value.Limit is { } limit) writer.WriteNumber("limit", limit);
        if (value.Counts) writer.WriteBoolean("counts", true);
        if (value.Scan is { } scan) writer.WriteNumber("scan", scan);
        writer.WriteEndObject();
    }
}
