using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OxQL.AspNetCore.Models;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Binding;

/// <summary>
/// Answers a contract 2 request whose body the host could not bind in the refusal envelope: 400
/// <c>validation_error</c> with a code and <c>params</c> a client can render
/// (<see cref="Codes.InvalidRequestBody"/>, or the stage's own code where one names the mistake
/// exactly), in place of the host's own answer to an invalid model state. Put it on a controller
/// or an action that takes an OxQL body (<see cref="QueryRequest"/>, <see cref="BatchRequest"/>,
/// <see cref="ExplainRequest"/>, or another model read from the body); the engine's controller
/// carries it.
/// <para>
/// A batch whose entries can be told apart is not refused for one of them: the entries that read
/// run, and each that does not is answered in its place of <c>results</c> with its own refusal.
/// </para>
/// <para>
/// A request under contract 1 (no <c>X-OxQL-Contract</c> header, or <c>1</c>) is not touched: the
/// host answers a body it cannot bind as it always has. Such a request is counted as a contract 1
/// request like every other (the log category <c>OxQL.Compat.Legacy</c>, <c>reason=unbound</c>).
/// </para>
/// <para>
/// It runs after the framework has answered a body that is no JSON media type (415) and before
/// everything that reads the model state: the framework's automatic 400 and any filter of the
/// host at the default order. A host needs to change nothing for it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OxQLRequestBodyAttribute : Attribute, IAsyncResourceFilter, IAsyncActionFilter, IOrderedFilter
{
    /// <summary>
    /// Where the filter stands among the filters of an action: after the framework's answer to an
    /// unsupported media type (-3000), before its automatic 400 for an invalid model state (-2000)
    /// and before a host's own filters (0).
    /// </summary>
    public const int FilterOrder = -2500;

    /// <summary>The <see cref="HttpContext.Items"/> key of the refusals of a batch's entries that did not read, by their index in <c>queries</c>.</summary>
    internal const string EntriesItem = "OxQL.Body.Entries";

    private const string RecorderItem = "OxQL.Body.Recorder";

    /// <inheritdoc/>
    public int Order => FilterOrder;

    /// <inheritdoc/>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        if (http.RequestServices.GetService<OxQLOptions>() is not { } options || BodyOf(context.ActionDescriptor) is not { } parameter)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var contract1 = OxQLQueryService.ContractOf(options, http) == 1;
        var limit = Math.Max(options.Limits.MaxRequestBytes, options.Explain.MaxRequestBytes);

        // A body within what a request may be long is read here, once, whether it declares its length or
        // not and whether the host has buffered it ahead or not: a name or a text in it that is no Unicode
        // text is answered before anything reads it as one. A body from the wire is handed to the model
        // binder from memory; a buffered one is read from its start and left where it stood. A longer body
        // is handed on as it came, what was read of it ahead of the rest: keeping a copy never stands
        // between a body and its binder.
        byte[]? bytes = null;
        var whole = true;

        if (IsUtf8(http.Request) && (http.Request.ContentLength ?? 0) <= limit)
        {
            if (http.Request.Body.CanSeek)
            {
                // What the binder reads of a buffer its host left elsewhere than at the start is not the body as it was written.
                whole = http.Request.Body.Position == 0;
                bytes = await RewoundAsync(http, limit).ConfigureAwait(false);
            }
            else if (await BufferedAsync(http, limit).ConfigureAwait(false) is { } read)
            {
                bytes = read;
                http.Request.Body = new MemoryStream(read, writable: false);
            }
        }

        if (bytes is not null)
        {
            if (RequestBodyReader.MayHoldBrokenText(bytes) && RequestBodyReader.BrokenTexts(bytes, DepthOf(http)) is { Count: > 0 } broken
                && (contract1 || OxQLQueryService.ContractRefusal(options, http) is null))
            {
                if (contract1)
                {
                    context.Result = await HostRefusalAsync(http, options, parameter).ConfigureAwait(false);
                    return;
                }

                // A batch whose own members read answers the entries that do not in their places and runs the others.
                if (whole && parameter.ParameterType == typeof(BatchRequest) && WithoutEntries(bytes, broken) is { } rest)
                {
                    http.Request.Body = new MemoryStream(rest.Body, writable: false);
                    http.Items[EntriesItem] = rest.Entries;
                }
                else
                {
                    context.Result = Refused(http, options, parameter, Refusal.Validation(broken.Select(text => RequestBodyReader.Broken(text.Path, text.Name)).ToList()));
                    return;
                }
            }
        }
        else if (!http.Request.Body.CanSeek)
        {
            // What the model binder reads is kept beside it, so a body that does not bind can be read
            // again and said in the terms of the wire. A body something ahead has buffered is read from there.
            var recorder = new RecordingStream(http.Request.Body, limit);

            http.Request.Body = recorder;
            http.Items[RecorderItem] = recorder;
        }

        // A body the binder met first (no declared length): a name that is no Unicode text stops the
        // read itself, with an error of the JSON reader that is no malformed JSON. It is the caller's
        // mistake and is answered as one, from the bytes the binder read.
        ResourceExecutedContext? executed = null;
        var stopped = false;

        try
        {
            executed = await next().ConfigureAwait(false);
            stopped = executed is { Exception: InvalidOperationException, ExceptionHandled: false };
        }
        catch (InvalidOperationException) when (!http.Response.HasStarted && BrokenIn(http) is not null)
        {
            stopped = true;
        }

        if (!stopped || http.Response.HasStarted || BrokenIn(http) is not { } texts)
            return;

        var result = contract1
            ? await HostRefusalAsync(http, options, parameter).ConfigureAwait(false)
            : Refused(http, options, parameter, Refusal.Validation(texts.Select(text => RequestBodyReader.Broken(text.Path, text.Name)).ToList()));

        if (executed is not null)
            executed.ExceptionHandled = true;

        await result.ExecuteResultAsync(executed ?? (ActionContext)context).ConfigureAwait(false);
    }

    /// <summary>The names and texts that are no Unicode text in the body the binder read itself; null when it kept none or holds none.</summary>
    private static List<(string Path, bool Name)>? BrokenIn(HttpContext http) =>
        http.Items[RecorderItem] is RecordingStream { Whole: true } recorded && IsUtf8(http.Request)
        && recorded.Recorded is var kept && RequestBodyReader.MayHoldBrokenText(kept) && RequestBodyReader.BrokenTexts(kept, DepthOf(http)) is { Count: > 0 } broken
            ? broken
            : null;

    /// <summary>How deep the host reads a body.</summary>
    private static int DepthOf(HttpContext http) =>
        http.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions.MaxDepth is > 0 and var depth ? depth
        : http.RequestServices.GetService<IOptions<JsonOptions>>() is null ? OxQLJson.MaxDepth
        : 64;

    private static bool IsUtf8(HttpRequest request) =>
        !MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || media.Encoding is null || media.Encoding.CodePage == Encoding.UTF8.CodePage;

    /// <summary>
    /// The whole body, read up to <paramref name="limit"/> bytes; null when it is longer, and the
    /// request's body is then what was read followed by the rest. The server's own cap on a body of
    /// undeclared length is set before the first byte is read, where the server has one.
    /// </summary>
    private static async Task<byte[]?> BufferedAsync(HttpContext http, int limit)
    {
        if (http.Request.ContentLength is null && http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap)
            cap.MaxRequestBodySize = limit;

        var body = http.Request.Body;
        var buffer = new byte[(int)Math.Min(limit + 1L, Math.Max(http.Request.ContentLength ?? 0, 16 * 1024))];
        var read = 0;

        while (true)
        {
            if (read == buffer.Length)
            {
                if (read > limit)
                    break;

                Array.Resize(ref buffer, (int)Math.Min(limit + 1L, buffer.Length * 2L));
            }

            var got = await body.ReadAsync(buffer.AsMemory(read), http.RequestAborted).ConfigureAwait(false);

            if (got == 0)
                return read == buffer.Length ? buffer : buffer[..read];

            read += got;
        }

        http.Request.Body = new ReplayStream(buffer, body);

        return null;
    }

    /// <summary>
    /// A body the host has buffered, read from its start up to <paramref name="limit"/> bytes and set
    /// back to where it stood; null when it is longer.
    /// </summary>
    private static async Task<byte[]?> RewoundAsync(HttpContext http, int limit)
    {
        var body = http.Request.Body;
        var stood = body.Position;

        try
        {
            body.Position = 0;

            var buffer = new byte[(int)Math.Min(limit + 1L, Math.Max(http.Request.ContentLength ?? 0, 16 * 1024))];
            var read = 0;

            while (true)
            {
                if (read == buffer.Length)
                {
                    if (read > limit)
                        return null;

                    Array.Resize(ref buffer, (int)Math.Min(limit + 1L, buffer.Length * 2L));
                }

                var got = await body.ReadAsync(buffer.AsMemory(read), http.RequestAborted).ConfigureAwait(false);

                if (got == 0)
                    return read == buffer.Length ? buffer : buffer[..read];

                read += got;
            }
        }
        finally
        {
            body.Position = stood;
        }
    }

    /// <summary>What was read of a body ahead of the rest of it, as one stream that reads forward only.</summary>
    private sealed class ReplayStream(byte[] ahead, Stream rest) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (position >= ahead.Length)
                return rest.Read(buffer);

            var taken = Math.Min(buffer.Length, ahead.Length - position);

            ahead.AsSpan(position, taken).CopyTo(buffer);
            position += taken;

            return taken;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            position >= ahead.Length ? rest.ReadAsync(buffer, cancellationToken) : ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The answer to a request without the contract header whose body holds a name or a text that is
    /// no Unicode text: the host's own 400, in the form it answers a body it cannot bind in.
    /// </summary>
    // LEGACY(compat.v1): the contract 1 half of the answer; remove with src/OxQL.AspNetCore/Legacy.
    private static async Task<IActionResult> HostRefusalAsync(HttpContext http, OxQLOptions options, Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter)
    {
        await Legacy.LegacyRoute.UnboundAsync(http, options, RouteOf(parameter.ParameterType)).ConfigureAwait(false);
        OxQLMetrics.Record(1, RouteOf(parameter.ParameterType), OxQLMetrics.NoEntity, OxQLMetrics.Unbound, TimeSpan.Zero);

        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            [RequestBodyReader.Root] = ["The request body holds a member name or a text that is no Unicode text: half of a surrogate pair, or bytes that are no UTF-8."],
        })
        {
            Status = StatusCodes.Status400BadRequest,
        };

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }

    /// <summary>
    /// A batch body with the entries that hold text that is no Unicode text written as <c>null</c>,
    /// and the refusal of each; null when such text lies outside the entries of <c>queries</c>, which
    /// refuses the batch whole.
    /// </summary>
    private static (byte[] Body, Dictionary<int, Refusal> Entries)? WithoutEntries(ReadOnlyMemory<byte> body, IReadOnlyList<(string Path, bool Name)> broken)
    {
        const string Queries = "$.queries[";
        var entries = new Dictionary<int, List<QueryValidationError>>();

        foreach (var (path, name) in broken)
        {
            if (!path.StartsWith(Queries, StringComparison.Ordinal) || path.IndexOf(']', Queries.Length) is not (> 0 and var close)
                || !int.TryParse(path.AsSpan(Queries.Length, close - Queries.Length), out var index))
                return null;

            if (!entries.TryGetValue(index, out var errors))
                entries[index] = errors = [];

            errors.Add(RequestBodyReader.Broken(RequestBodyReader.Root + path[(close + 1)..], name));
        }

        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = int.MaxValue });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        // The members are copied as they were written; only the entries in question are replaced.
        var text = new StringBuilder("{");

        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (text.Length > 1)
                text.Append(',');

            text.Append(JsonSerializer.Serialize(member.Name)).Append(':');

            if (member.Name != "queries" || member.Value.ValueKind != JsonValueKind.Array)
            {
                text.Append(member.Value.GetRawText());
                continue;
            }

            text.Append('[');

            var index = 0;

            foreach (var query in member.Value.EnumerateArray())
            {
                if (index > 0)
                    text.Append(',');

                text.Append(entries.ContainsKey(index++) ? "null" : query.GetRawText());
            }

            text.Append(']');
        }

        text.Append('}');

        return (Encoding.UTF8.GetBytes(text.ToString()), entries.ToDictionary(entry => entry.Key, entry => Refusal.Validation(entry.Value.Select(error => Entry(error, entry.Key)).ToList())));
    }

    /// <inheritdoc/>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        // A body the binder read itself (no declared length): a name or a text in it that is no Unicode
        // text is answered before anything reads it as one, whether the body bound or not.
        if (http.RequestServices.GetService<OxQLOptions>() is { } bodyOptions && BodyOf(context.ActionDescriptor) is { } bodyParameter
            && (OxQLQueryService.ContractOf(bodyOptions, http) == 1 || OxQLQueryService.ContractRefusal(bodyOptions, http) is null)
            && BrokenIn(http) is { } broken)
        {
            context.Result = OxQLQueryService.ContractOf(bodyOptions, http) == 1
                ? await HostRefusalAsync(http, bodyOptions, bodyParameter).ConfigureAwait(false)
                : Refused(http, bodyOptions, bodyParameter, Refusal.Validation(broken.Select(text => RequestBodyReader.Broken(text.Path, text.Name)).ToList()));
            return;
        }

        if (context.ModelState.IsValid
            || http.RequestServices.GetService<OxQLOptions>() is not { } options
            || BodyOf(context.ActionDescriptor) is not { } parameter
            // A body of another media type is the framework's to answer (415).
            || context.ModelState.Values.Any(entry => entry.Errors.Any(error => error.Exception is UnsupportedContentTypeException)))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (OxQLQueryService.ContractOf(options, http) == 1)
        {
            // LEGACY(compat.v1): the host answers; the request is counted where every contract 1 request is. Remove with src/OxQL.AspNetCore/Legacy.
            await next().ConfigureAwait(false);
            await Legacy.LegacyRoute.UnboundAsync(http, options, RouteOf(parameter.ParameterType)).ConfigureAwait(false);
            OxQLMetrics.Record(1, RouteOf(parameter.ParameterType), OxQLMetrics.NoEntity, OxQLMetrics.Unbound, TimeSpan.Zero);
            return;
        }

        if (OxQLQueryService.ContractRefusal(options, http) is { } unknownContract)
        {
            context.Result = Refused(http, options, parameter, unknownContract);
            return;
        }

        var json = http.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions ?? OxQLJson.Wire;
        var body = await WrittenAsync(http).ConfigureAwait(false);

        if (body is null)
        {
            // Nothing to read again (the body was longer than a request may be, or the stream gave none): said without a place.
            context.Result = Refused(http, options, parameter, Refusal.Validation([RequestBodyReader.Unreadable(RequestBodyReader.Root, null)]));
            return;
        }

        if (RequestBodyReader.Parse(body.Value, DepthOf(http), out var document) is { } unparsed)
        {
            context.Result = Refused(http, options, parameter, Refusal.Validation([unparsed]));
            return;
        }

        // A request that bound has only the host's validation against it; one that did not, only the read.
        var bound = context.ActionArguments.TryGetValue(parameter.Name, out var argument) && argument is not null;

        using (document)
        {
            var root = document!.RootElement;

            if (parameter.ParameterType == typeof(BatchRequest))
            {
                var (batch, entries) = RequestBodyReader.Batch(root, json);

                if (batch.Count == 0 && Rebuilt(root, entries.Keys, json) is { } rebuilt)
                {
                    if (bound)
                        foreach (var (index, wrong) in Validated(context.ModelState, parameter.Name))
                            if (index is { } entry && entry < rebuilt.Queries.Count && rebuilt.Queries[entry] is not null && !entries.ContainsKey(entry))
                                entries[entry] = wrong;
                            else if (index is null)
                                batch.AddRange(wrong);

                    if (batch.Count == 0 && entries.Count > 0)
                    {
                        // The batch reads: the entries that do not are answered in their places, the others run.
                        var answered = entries.ToDictionary(
                            entry => entry.Key,
                            entry => Refusal.Validation(entry.Value.Select(error => WithLimit(Entry(error, entry.Key), options)).ToList()));

                        // The entries answered before the body was bound keep their answers.
                        foreach (var (index, refusal) in http.Items[EntriesItem] as Dictionary<int, Refusal> ?? [])
                            answered[index] = refusal;

                        http.Items[EntriesItem] = answered;
                        context.ActionArguments[parameter.Name] = rebuilt with { Queries = [.. rebuilt.Queries.Select((query, index) => entries.ContainsKey(index) ? null! : query)] };
                        context.ModelState.Clear();

                        await next().ConfigureAwait(false);
                        return;
                    }
                }

                context.Result = Refused(http, options, parameter, Refusal.Validation(batch.Count > 0 ? batch : Whole(context.ModelState, parameter.Name, bound)));
                return;
            }

            var errors = parameter.ParameterType == typeof(QueryRequest) ? RequestBodyReader.Query(root, RequestBodyReader.Root, json)
                : parameter.ParameterType == typeof(ExplainRequest) ? RequestBodyReader.Explain(root, json)
                : RequestBodyReader.Other(root, parameter.ParameterType, json);

            context.Result = Refused(http, options, parameter, Refusal.Validation(errors.Count > 0 ? errors : Whole(context.ModelState, parameter.Name, bound)));
        }
    }

    /// <summary>The parameter of an action that is read from the body, or null when it has none.</summary>
    private static Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor? BodyOf(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
        action.Parameters.FirstOrDefault(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body);

    private static string RouteOf(Type model) =>
        model == typeof(BatchRequest) ? OxQLMetrics.RouteBatch
        : model == typeof(ExplainRequest) || model == typeof(ExplainBatchRequest) ? OxQLMetrics.RouteExplain
        : OxQLMetrics.RouteQuery;

    /// <summary>
    /// The refusal as the action's result, logged as the controller logs a refusal and counted as a
    /// request on the engine's meter: it never reaches the query service, where requests are counted.
    /// </summary>
    /// <summary>A refusal under a limit's code says the limit's value on this host, as every refusal of that limit does.</summary>
    private static QueryValidationError WithLimit(QueryValidationError error, OxQLOptions options) =>
        error is { Code: Codes.LookupLimitExceeded, Params: { } carried } && !carried.ContainsKey("max")
            ? error with { Params = new Dictionary<string, object?>(carried, StringComparer.Ordinal) { ["max"] = options.Limits.MaxLookupLimit } }
            : error;

    private static IActionResult Refused(HttpContext http, OxQLOptions options, Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter, Refusal refusal)
    {
        if (refusal.Errors is { } said)
            refusal = refusal with { Errors = [.. said.Select(error => WithLimit(error, options))] };

        OxQLMetrics.Record(
            OxQLQueryService.ContractOf(options, http),
            RouteOf(parameter.ParameterType),
            OxQLMetrics.NoEntity,
            OxQLMetrics.OutcomeOf(refusal),
            http.Items.TryGetValue(QueryTiming.StartItem, out var taken) && taken is long started ? System.Diagnostics.Stopwatch.GetElapsedTime(started) : TimeSpan.Zero);

        http.RequestServices.GetService<ILogger<OxQLRequestBodyAttribute>>()?.LogInformation(
            "OxQL refusal {Type} {Code}: {Message}",
            refusal.Type,
            LogText.Of(refusal.Errors?[0].Code),
            LogText.Of(refusal.Errors?[0].Message));

        return refusal.ToActionResult();
    }

    /// <summary>An entry's error with the index of its entry, so a refusal read apart from its place still names it.</summary>
    private static QueryValidationError Entry(QueryValidationError error, int index)
    {
        var parameters = new Dictionary<string, object?>(error.Params ?? new Dictionary<string, object?>(), StringComparer.Ordinal) { ["entry"] = index };

        return error with { Params = parameters };
    }

    /// <summary>
    /// The batch with every entry that reads, and null in the place of each that does not; null when
    /// the batch around them does not read either.
    /// </summary>
    private static BatchRequest? Rebuilt(JsonElement body, IEnumerable<int> unread, JsonSerializerOptions json)
    {
        var skipped = unread.ToHashSet();

        try
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                foreach (var member in body.EnumerateObject())
                {
                    writer.WritePropertyName(member.Name);

                    if (!string.Equals(member.Name, "queries", json.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || member.Value.ValueKind != JsonValueKind.Array)
                    {
                        member.Value.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartArray();

                    var index = 0;

                    foreach (var query in member.Value.EnumerateArray())
                    {
                        if (skipped.Contains(index++))
                            writer.WriteNullValue();
                        else
                            query.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return JsonSerializer.Deserialize<BatchRequest>(stream.ToArray(), json) is { Queries: not null } batch ? batch : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the host's validation of a bound request found beyond what the reader says: the places
    /// without a value where one is required, as JSON paths, by the entry of a batch they are in
    /// (null for the request itself). A key of the model state is the member's path in the model's
    /// names, which are the wire's names in another case.
    /// </summary>
    private static IEnumerable<(int? Entry, List<QueryValidationError> Errors)> Validated(ModelStateDictionary state, string parameter)
    {
        var found = new Dictionary<int, List<QueryValidationError>>();
        var whole = new List<QueryValidationError>();

        foreach (var (key, entry) in state)
        {
            if (entry.Errors.Count == 0)
                continue;

            var path = PathOf(key, parameter);
            const string Queries = "$.queries[";

            if (path.StartsWith(Queries, StringComparison.Ordinal) && path.IndexOf(']', Queries.Length) is > 0 and var close
                && int.TryParse(path.AsSpan(Queries.Length, close - Queries.Length), out var index))
            {
                if (!found.TryGetValue(index, out var errors))
                    found[index] = errors = [];

                errors.Add(RequestBodyReader.Valueless(RequestBodyReader.Root + path[(close + 1)..]));
            }
            else
            {
                whole.Add(RequestBodyReader.Valueless(path));
            }
        }

        foreach (var (index, errors) in found)
            yield return (index, errors);

        if (whole.Count > 0)
            yield return (null, whole);
    }

    /// <summary>
    /// What is left to say of a body the reader finds nothing in: every finding of the host's
    /// validation of the bound request, or, for a body that did not bind, that it does not read.
    /// </summary>
    private static List<QueryValidationError> Whole(ModelStateDictionary state, string parameter, bool bound)
    {
        if (!bound)
            return [RequestBodyReader.Unreadable(RequestBodyReader.Root, null)];

        var errors = Validated(state, parameter).SelectMany(found => found.Errors.Select(error => found.Entry is { } entry
            ? error with { Params = new Dictionary<string, object?>(error.Params!, StringComparer.Ordinal) { ["entry"] = entry } }
            : error)).ToList();

        return errors.Count > 0 ? errors : [RequestBodyReader.Unreadable(RequestBodyReader.Root, null)];
    }

    /// <summary>A model-state key as a JSON path: <c>Pipeline[0].Group</c> is <c>$.pipeline[0].group</c>; the parameter itself and a key that names nothing are the root.</summary>
    internal static string PathOf(string key, string parameter)
    {
        if (key.StartsWith("$", StringComparison.Ordinal))
            key = key[1..].TrimStart('.');

        if (key.Length == 0 || string.Equals(key, parameter, StringComparison.OrdinalIgnoreCase))
            return RequestBodyReader.Root;

        var path = new StringBuilder(RequestBodyReader.Root);

        foreach (var segment in key.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // Only plain member names and indexes are passed on; anything else ends the path where it stands.
            if (!segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '[' or ']'))
                break;

            path.Append('.').Append(char.ToLowerInvariant(segment[0])).Append(segment, 1, segment.Length - 1);
        }

        return path.ToString();
    }

    /// <summary>The body as the caller wrote it, in UTF-8; null when it was not kept whole.</summary>
    private static async Task<ReadOnlyMemory<byte>?> WrittenAsync(HttpContext http)
    {
        byte[] bytes;

        if (http.Items[RecorderItem] is RecordingStream recorder)
        {
            if (!recorder.Whole)
                return null;

            bytes = recorder.Recorded;
        }
        else if (http.Request.Body.CanSeek)
        {
            using var copy = new MemoryStream();

            http.Request.Body.Position = 0;
            await http.Request.Body.CopyToAsync(copy, http.RequestAborted).ConfigureAwait(false);
            bytes = copy.ToArray();
        }
        else
        {
            return null;
        }

        // The host reads a body in the charset its media type names; the reader takes UTF-8.
        if (MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var media) && media.Encoding is { } encoding && encoding.CodePage != Encoding.UTF8.CodePage)
            bytes = Encoding.Convert(encoding, Encoding.UTF8, bytes);

        return bytes;
    }

    /// <summary>The request's body stream, keeping what is read from it up to a length.</summary>
    private sealed class RecordingStream(Stream inner, int limit) : Stream
    {
        private readonly MemoryStream recorded = new();

        /// <summary>Whether everything read so far was kept: false once the body is longer than a request may be.</summary>
        public bool Whole { get; private set; } = true;

        public byte[] Recorded => recorded.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);

            Keep(buffer.AsSpan(offset, read));

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);

            Keep(buffer[..read]);

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

            Keep(buffer.AsSpan(offset, read));

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            Keep(buffer.Span[..read]);

            return read;
        }

        private void Keep(ReadOnlySpan<byte> read)
        {
            if (!Whole)
                return;

            if (recorded.Length + read.Length > limit)
            {
                Whole = false;
                recorded.SetLength(0);
                return;
            }

            recorded.Write(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
