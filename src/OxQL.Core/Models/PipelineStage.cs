using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// One stage of the pipeline: exactly one of the stage members is set. A stage object with
/// a key the engine does not know, or with more than one key, is carried through in
/// <see cref="Keys"/> so the binder can refuse it with a code.
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

    /// <summary>Every key the stage object carried, as written.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>The stage's kind, when it carries exactly one known key; null otherwise.</summary>
    [JsonIgnore]
    public string? Kind => Keys.Count == 1 && KnownKeys.Contains(Keys[0]) ? Keys[0] : null;

    /// <summary>The keys a stage may carry.</summary>
    public static readonly IReadOnlySet<string> KnownKeys =
        new HashSet<string>(StringComparer.Ordinal) { "match", "lookup", "resolve", "unwind", "group", "project", "sort", "page" };
}

internal sealed class PipelineStageConverter : JsonConverter<PipelineStage>
{
    public override PipelineStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for PipelineStage.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var keys = new List<string>();
        var stage = new PipelineStage();

        foreach (var property in root.EnumerateObject())
        {
            keys.Add(property.Name);

            switch (property.Name)
            {
                case "match":
                    stage = stage with { Match = JsonSerializer.Deserialize<MatchStage>(property.Value.GetRawText(), options) };
                    break;
                case "lookup":
                    stage = stage with { Lookup = JsonSerializer.Deserialize<LookupStage>(property.Value.GetRawText(), options) };
                    break;
                case "resolve":
                    stage = stage with { Resolve = JsonSerializer.Deserialize<ResolveStage>(property.Value.GetRawText(), options) };
                    break;
                case "unwind":
                    stage = stage with { Unwind = JsonSerializer.Deserialize<UnwindStage>(property.Value.GetRawText(), options) };
                    break;
                case "group":
                    stage = stage with { Group = JsonSerializer.Deserialize<GroupStage>(property.Value.GetRawText(), options) };
                    break;
                case "project":
                    stage = stage with { Project = JsonSerializer.Deserialize<ProjectStage>(property.Value.GetRawText(), options) };
                    break;
                case "sort":
                    stage = stage with { Sort = JsonSerializer.Deserialize<IReadOnlyList<SortField>>(property.Value.GetRawText(), options) };
                    break;
                case "page":
                    stage = stage with { Page = JsonSerializer.Deserialize<PageStage>(property.Value.GetRawText(), options) };
                    break;
            }
        }

        return stage with { Keys = keys };
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
