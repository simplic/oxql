using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using OxQL.AspNetCore.Models;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Batch;

/// <summary>
/// Caps the request body at <c>Limits:MaxRequestBytes</c> before it is read. A body whose
/// length is declared and too large is answered with the 413 refusal envelope
/// (<c>REQUEST_TOO_LARGE</c>); an undeclared length is capped at the server, which cuts the
/// read off at the same size.
/// </summary>
public sealed class RequestSizeFilter(OxQLOptions options) : IAsyncResourceFilter
{
    /// <inheritdoc/>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var max = options.Limits.MaxRequestBytes;
        var request = context.HttpContext.Request;

        if (request.ContentLength is { } length && length > max)
        {
            context.Result = Refusal.RequestTooLarge((int)Math.Min(length, int.MaxValue), max).ToActionResult();
            return;
        }

        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = max;

        await next();
    }
}
