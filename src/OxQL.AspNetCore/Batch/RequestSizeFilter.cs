using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.AspNetCore.Models;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Batch;

/// <summary>
/// Marks an action whose body is an explain request: <see cref="RequestSizeFilter"/> caps it at
/// <c>Explain:MaxRequestBytes</c> as well, so an oversized explain is refused before it is read.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExplainBodyAttribute : Attribute
{
}

/// <summary>
/// Caps the request body at <c>Limits:MaxRequestBytes</c> (and an explain body at
/// <c>Explain:MaxRequestBytes</c>, see <see cref="ExplainBodyAttribute"/>) before the model binder reads it. A
/// body whose length is declared and too large is answered with the 413 refusal envelope
/// (<c>REQUEST_TOO_LARGE</c>). An undeclared length (a chunked body) is capped in one of two
/// ways: when a middleware ahead of MVC has buffered the body, the stream is seekable and its
/// length is checked like a declared one; otherwise the cap is set at the server, which cuts
/// the read off at the same size. A host where neither is possible is told so in the log, once.
/// </summary>
public sealed class RequestSizeFilter(OxQLOptions options, ILogger<RequestSizeFilter>? logger = null) : IAsyncResourceFilter
{
    /// <summary>The hosts that have been told their undeclared-length bodies are not capped; the filter itself lives for one request.</summary>
    private static readonly ConditionalWeakTable<OxQLOptions, object> Warned = [];

    private readonly ILogger logger = logger ?? (ILogger)NullLogger<RequestSizeFilter>.Instance;

    /// <inheritdoc/>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // An explain route takes the tighter of the two caps (Explain:MaxRequestBytes).
        var max = context.ActionDescriptor.EndpointMetadata.OfType<ExplainBodyAttribute>().Any()
            ? Math.Min(options.Limits.MaxRequestBytes, options.Explain.MaxRequestBytes)
            : options.Limits.MaxRequestBytes;
        var request = context.HttpContext.Request;

        // A buffered body has been read already, so the server's cap can no longer be set; the
        // stream knows its length instead.
        var length = request.ContentLength ?? (request.Body.CanSeek ? request.Body.Length : (long?)null);

        if (length is { } known && known > max)
        {
            context.Result = Refusal.RequestTooLarge((int)Math.Min(known, int.MaxValue), max).ToActionResult();
            return;
        }

        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = max;
        else if (length is null && Warned.TryAdd(options, Warned))
            this.logger.LogWarning(
                "OxQL cannot cap a request body of undeclared length on this host: the server's body size limit is {State} and the body is not buffered. Limits:MaxRequestBytes ({Max}) holds for bodies that declare their length only.",
                feature is null ? "not available" : "read-only",
                max);

        await next();
    }
}
