// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Legacy;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Legacy;

/// <summary>
/// Decides which contract 1 requests the legacy pipeline answers, and hands it the request as the
/// caller wrote it.
/// <para>
/// With <c>Compat:Legacy:Always</c>, the default, that is every contract 1 request: a caller
/// without the contract header was written against the 1.1 engine and gets its answer.
/// </para>
/// <para>
/// With <c>Compat:Legacy:Always</c> off the compatibility rewrite is the first answer, as it was in
/// the published 2.0 engine: what it can express under contract 2 runs on the contract 2 engine.
/// What it cannot express and the 1.1 engine answered runs on the legacy pipeline:
/// <list type="bullet">
/// <item>a <c>lookup</c> or <c>resolve</c> stage in its v1 form (<c>localPath</c>, <c>foreignPath</c>, <c>source</c>:
/// <c>LEGACY_STAGE_UNSUPPORTED</c> from the rewrite);</item>
/// <item>a path the model does not declare in a stage that reads it (a match, an unwind, a projection,
/// a sort, a group): a stored member nobody modelled, the discriminator <c>_t</c>, or a name nothing
/// stores, which the 1.1 engine matched no row on;</item>
/// <item>a stage written after the page stage, which the 1.1 engine ran in the order written.</item>
/// </list>
/// </para>
/// <para>
/// Reachable only for a request without the contract header on a host with <c>Compat:Enabled</c>,
/// and only while <c>Compat:Legacy:Enabled</c>: <see cref="OxQLQueryService"/> calls it from its
/// contract 1 branch and nowhere else. A request that did not arrive over the query or the batch
/// route of the engine's controller has no body to read as written and is answered by the rewrite.
/// </para>
/// </summary>
public sealed class LegacyRoute
{
    /// <summary>The log category of a request the legacy pipeline answered; counting its lines says when the pipeline can go.</summary>
    public const string LogCategory = "OxQL.Compat.Legacy";

    private readonly ILegacyPipeline? pipeline;
    private readonly OxQLOptions options;
    private readonly IHttpContextAccessor? accessor;
    private readonly ILogger log;

    /// <summary>The route over the host's legacy pipeline, when one is registered.</summary>
    public LegacyRoute(OxQLOptions options, IServiceProvider services, IHttpContextAccessor? accessor = null, ILoggerFactory? loggerFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(services);
        pipeline = services.GetService(typeof(ILegacyPipeline)) as ILegacyPipeline;
        this.accessor = accessor;
        log = loggerFactory?.CreateLogger(LogCategory) ?? NullLogger.Instance;
    }

    /// <summary>
    /// Answers a contract 1 request: by <paramref name="compat"/>, the rewrite and the contract 2
    /// engine, unless the legacy pipeline is the one to answer it.
    /// </summary>
    public async Task<QueryOutcome> AnswerAsync(QueryRequest request, RequestContext context, Func<Task<QueryOutcome>> compat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compat);

        var legacy = options.Compat.Legacy;

        if (pipeline is null || !options.Compat.Enabled || !legacy.Enabled || context.Contract != 1 || context.Internal)
            return await compat().ConfigureAwait(false);

        if (legacy.Always)
        {
            if (await WrittenAsync(request, cancellationToken).ConfigureAwait(false) is { } whole)
                return await RunAsync(whole, request, context, "always", cancellationToken).ConfigureAwait(false);

            // The request did not come over a route that keeps its body (a controller of the host's own, say), or
            // the body is no JSON this reader takes: the rewrite answers, and the log says that it did.
            log.LogWarning(
                "OxQL.Compat.Legacy contract 1 request for {Entity} could not be read as written and is answered by the rewrite: reason=unreadable user={UserId} org={OrganisationId} correlation={CorrelationId}",
                LogText.Of(request.EntityType),
                context.UserId,
                context.Organisation,
                context.CorrelationId);

            return await compat().ConfigureAwait(false);
        }

        var outcome = await compat().ConfigureAwait(false);

        if (outcome is not QueryOutcome.Refused { Refusal: { Status: StatusCodes.Status400BadRequest } refusal } || Reason(request, refusal) is not { } reason)
            return outcome;

        return await WrittenAsync(request, cancellationToken).ConfigureAwait(false) is { } written
            ? await RunAsync(written, request, context, reason, cancellationToken).ConfigureAwait(false)
            : outcome;
    }

    private async Task<QueryOutcome> RunAsync(JsonElement written, QueryRequest request, RequestContext context, string reason, CancellationToken cancellationToken)
    {
        // The line is written whatever the pipeline ends in, a fault of its own included: who still
        // sends contract 1 is counted by it, with what the request cost and over which route it came.
        var refused = "INTERNAL_ERROR";
        var status = StatusCodes.Status500InternalServerError;
        var rows = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            var outcome = await pipeline!.ExecuteAsync(written, context, cancellationToken).ConfigureAwait(false);

            refused = (outcome as QueryOutcome.Refused)?.Refusal.Errors?[0].Code;
            status = outcome is QueryOutcome.Refused { Refusal.Status: var answered } ? answered : StatusCodes.Status200OK;
            rows = outcome is QueryOutcome.Success { Result.Items.Count: var served } ? served : 0;

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            refused = "CANCELLED";
            status = 499;
            throw;
        }
        finally
        {
            // The database's share is what the pipeline's aggregates took, when the request is measured.
            var spent = context.Timing?.Summary();

            log.LogInformation(
                "OxQL.Compat.Legacy contract 1 request for {Entity} answered by the legacy pipeline: reason={Reason} refused={Refused} user={UserId} org={OrganisationId} correlation={CorrelationId} route={Route} status={Status} rows={Rows} elapsedMs={ElapsedMs} dbMs={DbMs} dbCommands={DbCommands}",
                LogText.Of(request.EntityType),
                reason,
                refused,
                context.UserId,
                context.Organisation,
                context.CorrelationId,
                accessor?.HttpContext?.Items[LegacyBodyFilter.Item] is LegacyBody body ? body.Route : null,
                status,
                rows,
                Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2),
                spent?.DbMs,
                spent?.Commands);
        }
    }

    /// <summary>
    /// Writes the line of a contract 1 request whose body the host could not bind, so it never came
    /// to the pipeline: it is a contract 1 request all the same, and whoever counts the callers that
    /// still send no contract header counts this one too (<c>reason=unbound</c>). The host answered
    /// it, as it always has. Under <see cref="LogCategory"/> while the legacy pipeline answers
    /// contract 1, else under the compatibility category.
    /// </summary>
    internal static async Task UnboundAsync(HttpContext http, OxQLOptions options, string route)
    {
        if (!options.Compat.Enabled || http.RequestServices.GetService(typeof(ILoggerFactory)) is not ILoggerFactory loggers)
            return;

        var legacy = options.Compat.Legacy.Enabled && http.RequestServices.GetService(typeof(ILegacyPipeline)) is not null;
        var scope = http.RequestServices.GetService(typeof(Scope.IOxQLScopeProvider)) as Scope.IOxQLScopeProvider;
        Guid? organisation = null;
        string? user = null, correlation = null;

        try
        {
            if (scope is not null)
            {
                organisation = await scope.OrganisationAsync(http, http.RequestAborted).ConfigureAwait(false);
                user = scope.UserId(http);
                correlation = scope.CorrelationId(http);
            }
        }
        catch (Exception)
        {
            // The line is written without what the scope provider could not say.
        }

        var category = legacy ? LogCategory : OxQLQueryService.CompatLogCategory;

        loggers.CreateLogger(category).LogInformation(
            "{Category} contract 1 request with a body the host could not bind, answered by the host: reason={Reason} route={Route} user={UserId} org={OrganisationId} correlation={CorrelationId}",
            category,
            "unbound",
            route,
            user,
            organisation,
            LogText.Of(correlation ?? http.TraceIdentifier));
    }

    /// <summary>Why the legacy pipeline answers a request the rewrite or the binder refused, or null when the refusal stands.</summary>
    internal static string? Reason(QueryRequest request, Refusal refusal)
    {
        foreach (var error in refusal.Errors ?? [])
        {
            var written = error.Stage is { } stage && request.Pipeline is { } pipeline && stage >= 0 && stage < pipeline.Count ? pipeline[stage] : null;
            var kind = written?.Kind;

            // A join in its v1 form: the members contract 2 has none of. A join written in the contract 2 form
            // without the header stays the refusal that says so.
            if (error.Code == Codes.LegacyStageUnsupported && (V1(written?.Lookup?.Unknown, "localPath", "foreignPath") || V1(written?.Resolve?.Unknown, "source", "localPath")))
                return kind;

            // The 1.1 engine knew no model: a path was a stored name, in a match as in every other stage.
            if (error.Code == Codes.UnknownPath && kind is not null)
                return "path";

            // The 1.1 engine applied the page where it was written and every stage after it.
            if (error.Code == Codes.StageAfterPage)
                return "order";
        }

        return null;
    }

    private static bool V1(IReadOnlyList<string>? members, params string[] names) =>
        members is not null && members.Any(member => names.Contains(member, StringComparer.OrdinalIgnoreCase));

    // ---- the request as written --------------------------------------------------------------------

    /// <summary>
    /// The JSON of <paramref name="request"/> as the caller wrote it: the body of the query route, or
    /// the entry of the batch route's <c>queries</c> the request was bound from. Null when the request
    /// did not arrive over one of the two routes; the rewrite's answer then stands.
    /// </summary>
    private async Task<JsonElement?> WrittenAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (accessor?.HttpContext is not { } http || http.Items[LegacyBodyFilter.Item] is not LegacyBody body)
            return null;

        return await body.OfAsync(http, request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What a request bound from, kept until the legacy pipeline asks for the JSON it was written as.</summary>
internal sealed class LegacyBody(object? bound)
{
    private JsonDocument? document;
    private bool read;

    /// <summary>The route the request came over, as the log line and the meter name it: a batch's entries are of the batch route.</summary>
    public string Route => bound is BatchRequest ? OxQLMetrics.RouteBatch : OxQLMetrics.RouteQuery;

    public async Task<JsonElement?> OfAsync(HttpContext http, QueryRequest request, CancellationToken cancellationToken)
    {
        if (!read)
        {
            read = true;

            var stream = http.Request.Body;

            if (!stream.CanSeek)
                return null;

            stream.Position = 0;

            try
            {
                document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = OxQLJson.MaxDepth }, cancellationToken).ConfigureAwait(false);
                http.Response.RegisterForDispose(document);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (document is null)
            return null;

        var root = document.RootElement;

        if (bound is QueryRequest single)
            return ReferenceEquals(single, request) ? root : null;

        if (bound is BatchRequest { Queries: { } queries } && root.ValueKind == JsonValueKind.Object)
        {
            // A body that writes the member twice is bound from the last one, so that is the one read here.
            JsonElement? written = null;

            foreach (var member in root.EnumerateObject())
                if (string.Equals(member.Name, "queries", StringComparison.OrdinalIgnoreCase))
                    written = member.Value;

            if (written is not { ValueKind: JsonValueKind.Array } entries || entries.GetArrayLength() != queries.Count)
                return null;

            for (var index = 0; index < queries.Count; index++)
                if (ReferenceEquals(queries[index], request))
                    return entries[index];
        }

        return null;
    }
}

/// <summary>
/// Keeps the body of a contract 1 request readable after the host has bound it, so the legacy
/// pipeline can read the request as written: the typed request drops what contract 2 has no member
/// for (<c>localPath</c>, <c>foreignPath</c>, <c>convert</c>). It buffers nothing for a contract 2
/// request, and nothing at all once <c>Compat:Legacy:Enabled</c> is off.
/// </summary>
internal sealed class LegacyBodyFilter(OxQLOptions options) : IAsyncResourceFilter, IAsyncActionFilter
{
    /// <summary>The <see cref="HttpContext.Items"/> key the bound request is kept under.</summary>
    internal const string Item = "OxQL.Legacy.Body";

    private bool Applies(HttpContext http) =>
        options.Compat.Enabled && options.Compat.Legacy.Enabled && OxQLQueryService.ContractOf(options, http) == 1;

    /// <inheritdoc/>
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // Only the query and the batch route carry a request the legacy pipeline can answer.
        if (Applies(context.HttpContext) && context.ActionDescriptor.Parameters.Any(parameter => parameter.ParameterType == typeof(QueryRequest) || parameter.ParameterType == typeof(BatchRequest)))
            context.HttpContext.Request.EnableBuffering();

        return next();
    }

    /// <inheritdoc/>
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (Applies(context.HttpContext))
            context.HttpContext.Items[Item] = new LegacyBody(context.ActionArguments.Values.FirstOrDefault(argument => argument is QueryRequest or BatchRequest));

        return next();
    }
}
