using System.Text.Json;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Binding;

/// <summary>
/// Says why a request body does not read into the request model, in the terms of the wire: where
/// in the body (a JSON path), what stands there and what the place takes. It is asked only for a
/// body the host could not bind. What a request that binds pays is the copy the reader would
/// read from: a contract 2 request with a body has its bytes kept beside the model binder while
/// they are read (<see cref="OxQLRequestBodyAttribute"/>), once and at most as many as a request
/// may be long, and the copy is let go with the request.
/// <para>
/// It knows the places where a value of another JSON kind stops the typed read (the request's own
/// members, a stage that is no object, a condition, a sort, a group's keys and aggregates, the
/// whole numbers of a page, a projection and a lookup). Whatever it does not name it still
/// finds: after its own checks it reads the body into the model the way the host does
/// (<see cref="Trial"/>) and reports the stage, or the request, that does not read. No message
/// and no parameter ever carries a type name or the text of an exception.
/// </para>
/// </summary>
internal static class RequestBodyReader
{
    /// <summary>The root of a body as a JSON path.</summary>
    public const string Root = "$";

    public const string ReasonJson = "json";
    public const string ReasonEmpty = "empty";
    public const string ReasonDepth = "depth";
    public const string ReasonKind = "kind";
    public const string ReasonMissing = "missing";

    /// <summary>A member name or a text of the body is no Unicode text: half of a surrogate pair written as an escape, or bytes that are no UTF-8.</summary>
    public const string ReasonEncoding = "encoding";

    private static readonly string[] ExplainMembers = ["query", "remote", "include", "catalog", "shape", "budget"];

    // ---- the body as a whole ------------------------------------------------------------------

    /// <summary>
    /// The body as a document, or why it is none: empty, no well-formed JSON (with the line and the
    /// position in the line, both from 1), or nested deeper than the host reads.
    /// <para>
    /// How deep the body nests is found by one pass over its bytes that keeps a bit per level, and a
    /// document is built only of a body within <paramref name="maxDepth"/>: what a refusal costs is
    /// in proportion to the body's length, however deep the body nests.
    /// </para>
    /// </summary>
    public static QueryValidationError? Parse(ReadOnlyMemory<byte> utf8, int maxDepth, out JsonDocument? document)
    {
        document = null;

        if (IsBlank(utf8.Span))
            return new QueryValidationError
            {
                Code = Codes.InvalidRequestBody,
                Message = "The request body is empty; it carries a JSON object.",
                Params = new Dictionary<string, object?> { ["reason"] = ReasonEmpty },
            };

        // A byte order mark ahead of the body is passed over, as the host passes over it.
        utf8 = WithoutMark(utf8);

        try
        {
            // The body is read to its end without a depth of its own first: what is no JSON is said apart from how deep the host reads.
            if (Deepest(utf8.Span) > maxDepth)
                return new QueryValidationError
                {
                    Code = Codes.InvalidRequestBody,
                    Message = $"The request body nests deeper than {maxDepth} levels.",
                    Params = new Dictionary<string, object?> { ["reason"] = ReasonDepth, ["max"] = maxDepth },
                };

            document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = maxDepth });
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var position = (exception.BytePositionInLine ?? 0) + 1;

            return new QueryValidationError
            {
                Code = Codes.InvalidRequestBody,
                Message = $"The request body is not well-formed JSON at line {line}, position {position}.",
                Params = new Dictionary<string, object?> { ["reason"] = ReasonJson, ["line"] = line, ["position"] = position },
            };
        }

        return null;
    }

    /// <summary>
    /// How deep a body nests, counted as the reader counts it: an object or an array at the root is
    /// at 1, and a value that is neither is at the depth of what holds it. The body is read token by
    /// token to its end, and one that is no JSON fails as the reader fails.
    /// </summary>
    private static int Deepest(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = int.MaxValue });
        var deepest = 0;

        while (reader.Read())
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                deepest = Math.Max(deepest, reader.CurrentDepth + 1);

        return deepest;
    }

    // ---- text that is no text -----------------------------------------------------------------

    /// <summary>
    /// Whether a body may hold a name or a text that is no Unicode text: bytes that are no UTF-8, or
    /// an escape of half a surrogate pair (every such escape starts <c>\ud</c>). A body without
    /// either holds none, which is every body but a few: the full look (<see cref="BrokenTexts(ReadOnlyMemory{byte}, int)"/>)
    /// is taken only then.
    /// </summary>
    public static bool MayHoldBrokenText(ReadOnlySpan<byte> utf8) =>
        utf8.IndexOf("\\ud"u8) >= 0 || utf8.IndexOf("\\uD"u8) >= 0 || !System.Text.Unicode.Utf8.IsValid(utf8);

    /// <summary>
    /// The places of a body whose member name or text is no Unicode text, each as the JSON path of
    /// the text (of the object, for a name) and whether it is a name; empty when there is none, and
    /// null when the body is no JSON at all or nests deeper than <paramref name="maxDepth"/>, which
    /// is said in its own words. At most <see cref="MaxBrokenTexts"/> places are named, the first
    /// the body has.
    /// </summary>
    public static List<(string Path, bool Name)>? BrokenTexts(ReadOnlyMemory<byte> utf8, int maxDepth) => BrokenTexts(utf8, maxDepth, out _);

    /// <summary>
    /// The places of a body whose member name or text is no Unicode text, as
    /// <see cref="BrokenTexts(ReadOnlyMemory{byte}, int)"/> names them, and in
    /// <paramref name="unnamed"/> where the places past the ones it names lie.
    /// <para>
    /// The body is read once, token by token and to its end, to the depth the host reads a body
    /// to: a body that is answered for such a place is JSON throughout. The read takes time in
    /// proportion to the body's length, whatever the body nests, and keeps one entry per level it
    /// stands in. On top of that come a path for each place it names (as long as the place lies
    /// deep and its names are, for at most <see cref="MaxBrokenTexts"/> places) and a number for
    /// each entry of a batch that holds a place it does not name.
    /// </para>
    /// </summary>
    public static List<(string Path, bool Name)>? BrokenTexts(ReadOnlyMemory<byte> utf8, int maxDepth, out UnnamedTexts unnamed)
    {
        var broken = new List<(string Path, bool Name)>();
        var body = WithoutMark(utf8.Span);

        // Where the look stands: per level the index of the element (an array) or the name of the member (an object), as its place in the body.
        var levels = new List<Level>();
        var characters = System.Buffers.ArrayPool<char>.Shared.Rent(256);

        // The places past the ones that are named: the entries of a batch they lie in, and whether one lies elsewhere.
        List<int>? entries = null;
        var elsewhere = false;
        var member = (At: -1, Queries: false);

        unnamed = default;

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = maxDepth });

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        if (IsText(ref reader, ref characters))
                        {
                            levels[^1] = new Level(false, 0, (int)reader.TokenStartIndex, (int)reader.TokenStartIndex + reader.ValueSpan.Length + 2);
                        }
                        else
                        {
                            // The member is named by nothing: the object is the place, and what the member holds is passed over.
                            Found(body, levels.Count - 1, true);
                            reader.Skip();
                        }

                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        Element(levels);
                        levels.Add(new Level(reader.TokenType == JsonTokenType.StartArray, -1, 0, 0));
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        levels.RemoveAt(levels.Count - 1);
                        break;

                    case JsonTokenType.String:
                        Element(levels);

                        if (!IsText(ref reader, ref characters))
                            Found(body, levels.Count, false);

                        break;

                    default:
                        Element(levels);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Bytes that are no UTF-8 may stop the reader before it has read a text: they are still what is wrong.
            return System.Text.Unicode.Utf8.IsValid(body) ? null : [(Root, false)];
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(characters);
        }

        broken.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        unnamed = new UnnamedTexts(elsewhere, entries ?? []);

        return broken;

        // A value in an array is its next element.
        static void Element(List<Level> levels)
        {
            if (levels.Count > 0 && levels[^1] is { Array: true } array)
                levels[^1] = array with { Index = array.Index + 1 };
        }

        // A place under the first <count> levels: named while a refusal names more, and after that kept as where it lies.
        void Found(ReadOnlySpan<byte> json, int count, bool name)
        {
            if (broken.Count < MaxBrokenTexts)
            {
                broken.Add((PathOf(json, levels, count), name));
                return;
            }

            if (count < 2 || levels[0].Array || !levels[1].Array)
            {
                elsewhere = true;
                return;
            }

            // The name of the member the place lies under is read once, however many places lie under it.
            if (member.At != levels[0].NameStart)
                member = (levels[0].NameStart, JsonSerializer.Deserialize<string>(json[levels[0].NameStart..levels[0].NameEnd]) == BatchQueries);

            if (!member.Queries)
                elsewhere = true;
            else if (entries is not [.., var last] || last != levels[1].Index)
                (entries ??= []).Add(levels[1].Index);
        }
    }

    /// <summary>
    /// Where the places lie that a refusal does not name (those past <see cref="MaxBrokenTexts"/>):
    /// the indexes of the entries of a batch's <c>queries</c> that hold one, each once and in the
    /// order of the body, and whether one lies anywhere else.
    /// </summary>
    public readonly record struct UnnamedTexts(bool Elsewhere, IReadOnlyList<int> Entries);

    /// <summary>The member of a batch that carries its entries.</summary>
    public const string BatchQueries = "queries";

    /// <summary>A body without the byte order mark a client may write ahead of it.</summary>
    public static ReadOnlySpan<byte> WithoutMark(ReadOnlySpan<byte> utf8) => utf8.StartsWith(Mark) ? utf8[Mark.Length..] : utf8;

    /// <inheritdoc cref="WithoutMark(ReadOnlySpan{byte})"/>
    public static ReadOnlyMemory<byte> WithoutMark(ReadOnlyMemory<byte> utf8) => utf8.Span.StartsWith(Mark) ? utf8[Mark.Length..] : utf8;

    /// <summary>The UTF-8 byte order mark.</summary>
    private static ReadOnlySpan<byte> Mark => [0xEF, 0xBB, 0xBF];

    /// <summary>One level the look stands in: an array with the index of its element, or an object with where the name of its member is written (from its opening quote to past its closing one).</summary>
    private readonly record struct Level(bool Array, int Index, int NameStart, int NameEnd);

    /// <summary>The most places one refusal names.</summary>
    public const int MaxBrokenTexts = 20;

    /// <summary>The JSON path of the first <paramref name="count"/> levels.</summary>
    private static string PathOf(ReadOnlySpan<byte> utf8, List<Level> levels, int count)
    {
        var path = Root;

        for (var index = 0; index < count; index++)
        {
            var level = levels[index];

            // A name above a place that is found is text: the look passes over what a name that is none holds.
            path = level.Array
                ? $"{path}[{level.Index}]"
                : Join(path, JsonSerializer.Deserialize<string>(utf8[level.NameStart..level.NameEnd])!);
        }

        return path;
    }

    /// <summary>Whether the name or the text the reader stands on is Unicode text; nothing is kept of it.</summary>
    private static bool IsText(ref Utf8JsonReader reader, ref char[] characters)
    {
        if (!reader.ValueIsEscaped)
            return System.Text.Unicode.Utf8.IsValid(reader.ValueSpan);

        // An escape is never longer in characters than in the bytes it is written with.
        if (characters.Length < reader.ValueSpan.Length)
        {
            System.Buffers.ArrayPool<char>.Shared.Return(characters);
            characters = System.Buffers.ArrayPool<char>.Shared.Rent(reader.ValueSpan.Length);
        }

        try
        {
            reader.CopyString(characters);

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A name or a text that is no Unicode text, said without its place: one past the places a refusal names.</summary>
    public static QueryValidationError Broken() => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = "A member name or a text of the request is no Unicode text: it holds half of a surrogate pair, or bytes that are no UTF-8.",
        Params = new Dictionary<string, object?> { ["reason"] = ReasonEncoding },
    };

    /// <summary>A name or a text that is no Unicode text, at its place.</summary>
    public static QueryValidationError Broken(string at, bool name) => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = name
            ? $"A member name in '{at}' is no Unicode text: it holds half of a surrogate pair, or bytes that are no UTF-8."
            : $"The text at '{at}' is no Unicode text: it holds half of a surrogate pair, or bytes that are no UTF-8.",
        Params = new Dictionary<string, object?> { ["reason"] = ReasonEncoding, ["path"] = at, ["in"] = name ? "name" : "value" },
    };

    private static bool IsBlank(ReadOnlySpan<byte> utf8)
    {
        // A byte order mark ahead of nothing is nothing.
        utf8 = WithoutMark(utf8);

        foreach (var value in utf8)
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;

        return true;
    }

    // ---- the three bodies ---------------------------------------------------------------------

    /// <summary>Why <paramref name="query"/> does not read as a query; empty when it does.</summary>
    public static List<QueryValidationError> Query(JsonElement query, string at, JsonSerializerOptions options)
    {
        var errors = new List<QueryValidationError>();

        if (!Takes(query, at, "object", errors, null))
            return errors;

        var loose = options.PropertyNameCaseInsensitive;

        if (Member(query, "entityType", loose) is not { } entity)
            errors.Add(Absent(Join(at, "entityType"), "a query carries an entityType and a pipeline"));
        else
            Takes(entity, Join(at, "entityType"), "string", errors, null);

        if (Member(query, "pipeline", loose) is not { } pipeline)
        {
            errors.Add(Absent(Join(at, "pipeline"), "a query carries an entityType and a pipeline"));
        }
        else if (Takes(pipeline, Join(at, "pipeline"), "array", errors, null))
        {
            var index = 0;

            foreach (var stage in pipeline.EnumerateArray())
            {
                var before = errors.Count;
                var place = Join(at, "pipeline") + $"[{index}]";

                Stage(stage, place, index, loose, errors);

                // A stage the checks above pass and the model still does not read is said as a whole.
                if (errors.Count == before && !Reads<PipelineStage>(stage, options))
                    errors.Add(Unreadable(stage.ValueKind == JsonValueKind.Object && Only(stage) is { } key ? Join(place, key) : place, index));

                index++;
            }
        }

        if (Member(query, "variables", loose) is { ValueKind: not JsonValueKind.Null } variables)
            Takes(variables, Join(at, "variables"), "object", errors, null);

        if (Member(query, "strict", loose) is { ValueKind: not JsonValueKind.Null } strict)
            Takes(strict, Join(at, "strict"), "boolean", errors, null);

        if (Member(query, "keyedBy", loose) is { ValueKind: not JsonValueKind.Null } keyedBy && Takes(keyedBy, Join(at, "keyedBy"), "object", errors, null))
        {
            foreach (var name in new[] { "path", "references", "rows" })
                if (Member(keyedBy, name, loose) is { ValueKind: not JsonValueKind.Null } text)
                    Takes(text, Join(Join(at, "keyedBy"), name), "string", errors, null);

            if (Member(keyedBy, "perKey", loose) is { ValueKind: not JsonValueKind.Null } perKey)
                Takes(perKey, Join(Join(at, "keyedBy"), "perKey"), "integer", errors, null);
        }

        if (errors.Count == 0 && !Reads<QueryRequest>(query, options))
            errors.Add(Unreadable(at, null));

        return errors;
    }

    /// <summary>
    /// Why a batch body does not read: what is wrong with the batch itself, and per entry of
    /// <c>queries</c> what is wrong with that query (its paths start at the entry). An entry that is
    /// <c>null</c> is none of this reader's: the batch answers it in its place.
    /// </summary>
    public static (List<QueryValidationError> Batch, Dictionary<int, List<QueryValidationError>> Entries) Batch(JsonElement body, JsonSerializerOptions options)
    {
        var errors = new List<QueryValidationError>();
        var entries = new Dictionary<int, List<QueryValidationError>>();

        if (!Takes(body, Root, "object", errors, null))
            return (errors, entries);

        var loose = options.PropertyNameCaseInsensitive;

        if (Member(body, "maxTimeMs", loose) is { ValueKind: not JsonValueKind.Null } ceiling)
            Takes(ceiling, Join(Root, "maxTimeMs"), "integer", errors, null);

        if (Member(body, "queries", loose) is not { } queries)
        {
            errors.Add(Absent(Join(Root, "queries"), "a batch carries a queries array"));
        }
        else if (Takes(queries, Join(Root, "queries"), "array", errors, null))
        {
            var index = 0;

            foreach (var query in queries.EnumerateArray())
            {
                if (query.ValueKind != JsonValueKind.Null && Query(query, Root, options) is { Count: > 0 } wrong)
                    entries[index] = wrong;

                index++;
            }
        }

        return (errors, entries);
    }

    /// <summary>Why an explain body does not read: a plain query, or the envelope around one.</summary>
    public static List<QueryValidationError> Explain(JsonElement body, JsonSerializerOptions options)
    {
        var errors = new List<QueryValidationError>();

        if (!Takes(body, Root, "object", errors, null))
            return errors;

        // A body with entityType at the top is a plain query, read as one.
        if (body.TryGetProperty("entityType", out _))
            return Query(body, Root, options);

        foreach (var member in body.EnumerateObject())
            if (!ExplainMembers.Contains(member.Name, StringComparer.Ordinal))
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownRequestMember,
                    Message = $"'{member.Name}' is not a member of an explain envelope; it carries query, include, shape, remote and catalog.",
                    Params = new Dictionary<string, object?> { ["member"] = member.Name, ["members"] = new[] { member.Name }, ["reason"] = "unknown", ["path"] = Join(Root, member.Name) },
                });

        if (!body.TryGetProperty("query", out var query))
            errors.Add(Absent(Join(Root, "query"), "an explain envelope carries the query under 'query'"));
        else if (Takes(query, Join(Root, "query"), "object", errors, null))
            errors.AddRange(Query(query, Join(Root, "query"), options));

        if (Written(body, "remote") is { } remote)
            Takes(remote, Join(Root, "remote"), "string", errors, null);

        if (Written(body, "include") is { } include && Takes(include, Join(Root, "include"), "array", errors, null))
            Each(include, Join(Root, "include"), "string", errors);

        if (Written(body, "catalog") is { } catalog && Takes(catalog, Join(Root, "catalog"), "array", errors, null))
            Each(catalog, Join(Root, "catalog"), "object", errors);

        if (Written(body, "shape") is { } shape && Takes(shape, Join(Root, "shape"), "object", errors, null))
            foreach (var member in shape.EnumerateObject())
            {
                var place = Join(Join(Root, "shape"), member.Name);

                if (member.Name != "depth")
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.UnknownRequestMember,
                        Message = $"'{member.Name}' is not a member of 'shape'; it carries depth.",
                        Params = new Dictionary<string, object?> { ["member"] = "shape." + member.Name, ["members"] = new[] { "shape." + member.Name }, ["reason"] = "unknown", ["path"] = place },
                    });
                else if (Takes(member.Value, place, "integer", errors, null) && member.Value.GetInt32() < 1)
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.InvalidRequestBody,
                        Message = $"'{place}' is {member.Value.GetInt32()}; it is a whole number of at least 1.",
                        Params = new Dictionary<string, object?> { ["reason"] = ReasonKind, ["path"] = place, ["expected"] = "integer", ["actual"] = "number", ["min"] = 1 },
                    });
            }

        if (Written(body, "budget") is { } budget && Takes(budget, Join(Root, "budget"), "object", errors, null))
            foreach (var name in new[] { "ms", "calls" })
            {
                if (!budget.TryGetProperty(name, out var number))
                    errors.Add(Absent(Join(Join(Root, "budget"), name), "'budget' carries ms and calls"));
                else
                    Takes(number, Join(Join(Root, "budget"), name), "integer", errors, null);
            }

        if (errors.Count == 0 && !Reads<ExplainRequest>(body, options))
            errors.Add(Unreadable(Root, null));

        return errors;
    }

    /// <summary>Why a body of another model does not read: as a whole, when it does not.</summary>
    public static List<QueryValidationError> Other(JsonElement body, Type model, JsonSerializerOptions options)
    {
        var errors = new List<QueryValidationError>();

        if (Takes(body, Root, "object", errors, null) && !Trial(() => body.Deserialize(model, options)))
            errors.Add(Unreadable(Root, null));

        return errors;
    }

    // ---- stages -------------------------------------------------------------------------------

    private static void Stage(JsonElement stage, string at, int index, bool loose, List<QueryValidationError> errors)
    {
        // A null element is a stage without a kind, which the binder refuses under its own code.
        if (stage.ValueKind == JsonValueKind.Null || !Takes(stage, at, "object", errors, index))
            return;

        foreach (var member in stage.EnumerateObject())
        {
            var value = member.Value;
            var place = Join(at, member.Name);

            if (value.ValueKind == JsonValueKind.Null)
                continue;

            switch (member.Name)
            {
                case "match":
                    Condition(value, place, index, errors);
                    break;

                case "sort":
                    Takes(value, place, "array", errors, index);
                    break;

                case "page" when value.ValueKind == JsonValueKind.Object:
                    foreach (var number in value.EnumerateObject())
                        if (number.Name is "limit" or "offset" && number.Value.ValueKind == JsonValueKind.Number && !number.Value.TryGetInt32(out _))
                            errors.Add(new QueryValidationError
                            {
                                Code = Codes.InvalidPageLimit,
                                Message = $"'{number.Name}' of page is a whole number.",
                                Stage = index,
                                Params = new Dictionary<string, object?> { ["stage"] = "page", ["member"] = number.Name, ["reason"] = ReasonKind, ["path"] = Join(place, number.Name), ["expected"] = "integer", ["actual"] = "number" },
                            });
                    break;

                case "project":
                    if (Takes(value, place, "object", errors, index))
                        Projection(value, place, index, errors);
                    break;

                case "unwind" when value.ValueKind == JsonValueKind.Object:
                    Texts(value, place, index, errors, "path", "as", "includeIndex");
                    break;

                case "group" when value.ValueKind == JsonValueKind.Object:
                    Group(value, place, index, loose, errors);
                    break;

                case "lookup":
                    if (!Takes(value, place, "object", errors, index))
                        break;

                    Texts(value, place, index, errors, "from", "path", "as");

                    foreach (var part in value.EnumerateObject())
                    {
                        if (part.Name == "filter" && part.Value.ValueKind != JsonValueKind.Null)
                            Condition(part.Value, Join(place, "filter"), index, errors);
                        else if (part.Name == "limit" && part.Value.ValueKind == JsonValueKind.Number && !part.Value.TryGetInt32(out _))
                            errors.Add(new QueryValidationError
                            {
                                Code = Codes.LookupLimitExceeded,
                                Message = "'limit' of lookup is a whole number.",
                                Stage = index,
                                Params = new Dictionary<string, object?> { ["stage"] = "lookup", ["member"] = "limit", ["limit"] = "maxLookupLimit", ["reason"] = ReasonKind, ["path"] = Join(place, "limit"), ["expected"] = "integer", ["actual"] = "number" },
                            });
                    }

                    break;

                case "resolve":
                    if (!Takes(value, place, "object", errors, index))
                        break;

                    Texts(value, place, index, errors, "path", "as");

                    foreach (var part in value.EnumerateObject())
                        if (part.Name == "filter" && part.Value.ValueKind != JsonValueKind.Null)
                            Condition(part.Value, Join(place, "filter"), index, errors);

                    break;
            }
        }
    }

    /// <summary>A condition is an object; <c>and</c> and <c>or</c> take an array of conditions, <c>not</c> and <c>any</c> a condition.</summary>
    private static void Condition(JsonElement condition, string at, int stage, List<QueryValidationError> errors)
    {
        if (!Takes(condition, at, "object", errors, stage))
            return;

        foreach (var member in condition.EnumerateObject())
        {
            var place = Join(at, member.Name);

            switch (member.Name)
            {
                case "and" or "or":
                    if (Takes(member.Value, place, "array", errors, stage))
                    {
                        var index = 0;

                        foreach (var item in member.Value.EnumerateArray())
                            Condition(item, $"{place}[{index++}]", stage, errors);
                    }

                    break;

                case "not":
                    Condition(member.Value, place, stage, errors);
                    break;

                default:
                    if (member.Value.ValueKind == JsonValueKind.Object)
                        foreach (var written in member.Value.EnumerateObject())
                            if (written.Name == "any" && written.Value.ValueKind == JsonValueKind.Object)
                                Condition(written.Value, Join(place, "any"), stage, errors);

                    break;
            }
        }
    }

    private static void Projection(JsonElement fields, string at, int stage, List<QueryValidationError> errors)
    {
        foreach (var member in fields.EnumerateObject())
        {
            var place = Join(at, member.Name);

            if (member.Value.ValueKind == JsonValueKind.Object)
                Projection(member.Value, place, stage, errors);
            else if (member.Value.ValueKind == JsonValueKind.Number)
                Takes(member.Value, place, "integer", errors, stage);
        }
    }

    private static void Group(JsonElement group, string at, int stage, bool loose, List<QueryValidationError> errors)
    {
        foreach (var member in group.EnumerateObject())
        {
            var place = Join(at, member.Name);

            if (member.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (member.Name == "by" && Takes(member.Value, place, "array", errors, stage))
            {
                var index = 0;

                foreach (var key in member.Value.EnumerateArray())
                {
                    var entry = $"{place}[{index++}]";

                    if (key.ValueKind != JsonValueKind.Object)
                        continue;

                    Texts(key, entry, stage, errors, "path", "as");

                    foreach (var part in key.EnumerateObject())
                        if (part.Name == "dateTrunc" && part.Value.ValueKind != JsonValueKind.Null && Takes(part.Value, Join(entry, "dateTrunc"), "object", errors, stage))
                            DateTrunc(part.Value, Join(entry, "dateTrunc"), stage, loose, errors);
                }
            }
            else if (member.Name == "fields" && Takes(member.Value, place, "object", errors, stage))
            {
                foreach (var field in member.Value.EnumerateObject())
                {
                    var entry = Join(place, field.Name);

                    if (field.Value.ValueKind == JsonValueKind.Null || !Takes(field.Value, entry, "object", errors, stage))
                        continue;

                    // An aggregate is read by its first member: count: true, or a function over an argument.
                    foreach (var function in field.Value.EnumerateObject())
                    {
                        if (!(function.Name == "count" && function.Value.ValueKind == JsonValueKind.True))
                            Argument(function.Value, Join(entry, function.Name), stage, errors);

                        break;
                    }
                }
            }
        }
    }

    private static void DateTrunc(JsonElement trunc, string at, int stage, bool loose, List<QueryValidationError> errors)
    {
        foreach (var name in new[] { "path", "unit" })
        {
            if (Member(trunc, name, loose) is not { } written)
                errors.Add(Absent(Join(at, name), "a dateTrunc carries a path and a unit", stage));
            else
                Takes(written, Join(at, name), "string", errors, stage);
        }

        foreach (var name in new[] { "timezone", "weekStart" })
            if (Member(trunc, name, loose) is { ValueKind: not JsonValueKind.Null } written)
                Takes(written, Join(at, name), "string", errors, stage);
    }

    /// <summary>An aggregate's argument: a path as text, or an object naming a path, a variable, a literal or an operator over arguments.</summary>
    private static void Argument(JsonElement argument, string at, int stage, List<QueryValidationError> errors)
    {
        if (argument.ValueKind != JsonValueKind.Object)
            return;

        if (argument.TryGetProperty("path", out var path))
        {
            if (path.ValueKind != JsonValueKind.Null)
                Takes(path, Join(at, "path"), "string", errors, stage);

            return;
        }

        if (argument.TryGetProperty("$var", out var variable))
        {
            if (variable.ValueKind != JsonValueKind.Null)
                Takes(variable, Join(at, "$var"), "string", errors, stage);

            return;
        }

        if (argument.TryGetProperty("literal", out _))
            return;

        foreach (var member in argument.EnumerateObject())
        {
            if (!QueryExpression.ArithmeticOperators.Contains(member.Name))
                continue;

            if (member.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;

                foreach (var operand in member.Value.EnumerateArray())
                    Argument(operand, $"{Join(at, member.Name)}[{index++}]", stage, errors);
            }

            return;
        }
    }

    /// <summary>The members of <paramref name="value"/> that are text or null.</summary>
    private static void Texts(JsonElement value, string at, int stage, List<QueryValidationError> errors, params string[] names)
    {
        foreach (var member in value.EnumerateObject())
            if (names.Contains(member.Name, StringComparer.Ordinal) && member.Value.ValueKind != JsonValueKind.Null)
                Takes(member.Value, Join(at, member.Name), "string", errors, stage);
    }

    private static void Each(JsonElement array, string at, string expected, List<QueryValidationError> errors)
    {
        var index = 0;

        foreach (var item in array.EnumerateArray())
            Takes(item, $"{at}[{index++}]", expected, errors, null);
    }

    // ---- the pieces ---------------------------------------------------------------------------

    /// <summary>The last member of that name, as the model reads it; null when there is none.</summary>
    private static JsonElement? Member(JsonElement value, string name, bool loose)
    {
        JsonElement? found = null;

        foreach (var member in value.EnumerateObject())
            if (string.Equals(member.Name, name, loose ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                found = member.Value;

        return found;
    }

    /// <summary>A member of an explain envelope that is written and not null.</summary>
    private static JsonElement? Written(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>The one key of a stage object, or null when it has none or several.</summary>
    private static string? Only(JsonElement stage)
    {
        string? only = null;

        foreach (var member in stage.EnumerateObject())
        {
            if (only is not null)
                return null;

            only = member.Name;
        }

        return only;
    }

    /// <summary>Whether <paramref name="value"/> is of the kind the place takes; says so when it is not.</summary>
    private static bool Takes(JsonElement value, string at, string expected, List<QueryValidationError> errors, int? stage)
    {
        var fits = expected switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            _ => true,
        };

        if (fits)
            return true;

        var actual = KindOf(value);

        errors.Add(new QueryValidationError
        {
            Code = Codes.InvalidRequestBody,
            Message = expected == "integer" && actual == "number"
                ? $"'{at}' is no whole number a request reads; it takes an integer."
                : $"'{at}' is {Article(actual)}; it takes {Article(expected)}.",
            Stage = stage,
            Params = new Dictionary<string, object?> { ["reason"] = ReasonKind, ["path"] = at, ["expected"] = expected, ["actual"] = actual },
        });

        return false;
    }

    /// <summary>The JSON kind of a value, as a request names kinds.</summary>
    public static string KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };

    private static string Article(string kind) => kind switch
    {
        "null" => "null",
        "object" or "array" or "integer" => "an " + kind,
        _ => "a " + kind,
    };

    private static QueryValidationError Absent(string at, string carries, int? stage = null) => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = $"'{at}' is missing; {carries}.",
        Stage = stage,
        Params = new Dictionary<string, object?> { ["reason"] = ReasonMissing, ["path"] = at },
    };

    /// <summary>A place the model does not read, where no check above says which value of it is of another kind.</summary>
    public static QueryValidationError Unreadable(string at, int? stage) => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = $"'{at}' cannot be read: a value in it is of another JSON kind than its place takes.",
        Stage = stage,
        Params = new Dictionary<string, object?> { ["reason"] = ReasonKind, ["path"] = at },
    };

    /// <summary>A member that has no value where one is required, as the host's validation of a bound request says it.</summary>
    public static QueryValidationError Valueless(string at) => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = $"'{at}' has no value; the request needs one there.",
        Params = new Dictionary<string, object?> { ["reason"] = ReasonMissing, ["path"] = at },
    };

    /// <summary>A member under a place: <c>$.pipeline</c>, and in brackets where the name is more than a plain word (<c>$.match['items.name']</c>).</summary>
    public static string Join(string at, string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] is '_' or '$') && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$')
            ? $"{at}.{name}"
            : $"{at}['{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static bool Reads<T>(JsonElement value, JsonSerializerOptions options) => Trial(() => value.Deserialize<T>(options));

    /// <summary>
    /// Whether a typed read succeeds. A read that fails says so with one of these, by the reader's
    /// own rules or a converter's; what it says is never passed on.
    /// </summary>
    private static bool Trial(Action read)
    {
        try
        {
            read();

            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            return false;
        }
    }
}
