using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a single stage in the query pipeline.
/// </summary>
[JsonConverter(typeof(PipelineStageConverter))]
public sealed record PipelineStage
{
    public MatchStage? Match { get; init; }
    public LookupStage? Lookup { get; init; }
    public ResolveStage? Resolve { get; init; }
    public UnwindStage? Unwind { get; init; }
    public GroupStage? Group { get; init; }
    public ProjectStage? Project { get; init; }
    public IReadOnlyList<SortField>? Sort { get; init; }
    public PageStage? Page { get; init; }
}

internal sealed class PipelineStageConverter : JsonConverter<PipelineStage>
{
    public override PipelineStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for PipelineStage.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var stage = new PipelineStage();

        if (root.TryGetProperty("match", out var matchEl))
            stage = stage with { Match = JsonSerializer.Deserialize<MatchStage>(matchEl.GetRawText(), options) };

        if (root.TryGetProperty("lookup", out var lookupEl))
            stage = stage with { Lookup = JsonSerializer.Deserialize<LookupStage>(lookupEl.GetRawText(), options) };

        if (root.TryGetProperty("resolve", out var resolveEl))
            stage = stage with { Resolve = JsonSerializer.Deserialize<ResolveStage>(resolveEl.GetRawText(), options) };

        if (root.TryGetProperty("unwind", out var unwindEl))
            stage = stage with { Unwind = JsonSerializer.Deserialize<UnwindStage>(unwindEl.GetRawText(), options) };

        if (root.TryGetProperty("group", out var groupEl))
            stage = stage with { Group = JsonSerializer.Deserialize<GroupStage>(groupEl.GetRawText(), options) };

        if (root.TryGetProperty("project", out var projectEl))
            stage = stage with { Project = JsonSerializer.Deserialize<ProjectStage>(projectEl.GetRawText(), options) };

        if (root.TryGetProperty("sort", out var sortEl))
            stage = stage with { Sort = JsonSerializer.Deserialize<IReadOnlyList<SortField>>(sortEl.GetRawText(), options) };

        if (root.TryGetProperty("page", out var pageEl))
            stage = stage with { Page = JsonSerializer.Deserialize<PageStage>(pageEl.GetRawText(), options) };

        return stage;
    }

    public override void Write(Utf8JsonWriter writer, PipelineStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Match is not null)
        {
            writer.WritePropertyName("match");
            JsonSerializer.Serialize(writer, value.Match, options);
        }

        if (value.Lookup is not null)
        {
            writer.WritePropertyName("lookup");
            JsonSerializer.Serialize(writer, value.Lookup, options);
        }

        if (value.Resolve is not null)
        {
            writer.WritePropertyName("resolve");
            JsonSerializer.Serialize(writer, value.Resolve, options);
        }

        if (value.Unwind is not null)
        {
            writer.WritePropertyName("unwind");
            JsonSerializer.Serialize(writer, value.Unwind, options);
        }

        if (value.Group is not null)
        {
            writer.WritePropertyName("group");
            JsonSerializer.Serialize(writer, value.Group, options);
        }

        if (value.Project is not null)
        {
            writer.WritePropertyName("project");
            JsonSerializer.Serialize(writer, value.Project, options);
        }

        if (value.Sort is not null)
        {
            writer.WritePropertyName("sort");
            JsonSerializer.Serialize(writer, value.Sort, options);
        }

        if (value.Page is not null)
        {
            writer.WritePropertyName("page");
            JsonSerializer.Serialize(writer, value.Page, options);
        }

        writer.WriteEndObject();
    }
}
