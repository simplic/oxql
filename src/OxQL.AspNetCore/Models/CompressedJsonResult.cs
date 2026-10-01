using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace OxQL.AspNetCore.Models;

/// <summary>
/// The content coding of an answer a caller reads often and that compresses well (an explain answer, a
/// schema document): chosen from the request's <c>Accept-Encoding</c>, applied to the finished body.
/// It is done here, per answer, and not by a middleware: the host decides nothing, no other route
/// changes, and the cost is known (the fastest level; an explain answer of 40 KB is a fraction of a
/// millisecond). A caller that names no coding gets the body as it is.
/// </summary>
public static class WireCompression
{
    /// <summary>A body below this is sent as it is: its coding would save less than a packet.</summary>
    public const int MinBytes = 1_024;

    /// <summary>The <c>Content-Encoding</c> of Brotli.</summary>
    public const string Brotli = "br";

    /// <summary>The <c>Content-Encoding</c> of gzip.</summary>
    public const string Gzip = "gzip";

    /// <summary>
    /// The coding to answer <paramref name="request"/> with for a body of <paramref name="length"/> bytes:
    /// Brotli when the caller accepts it, else gzip, else none (null). A coding with <c>q=0</c> is refused;
    /// <c>*</c> accepts any.
    /// </summary>
    public static string? EncodingFor(HttpRequest request, int length)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (length < MinBytes)
            return null;

        var accepted = request.GetTypedHeaders().AcceptEncoding;

        if (accepted is null || accepted.Count == 0)
            return null;

        bool Accepts(string coding) =>
            accepted.FirstOrDefault(each => each.Value.Equals(coding, StringComparison.OrdinalIgnoreCase)) is { } named
                ? named.Quality is not 0
                : accepted.Any(each => each.Value == "*" && each.Quality is not 0);

        return Accepts(Brotli) ? Brotli : Accepts(Gzip) ? Gzip : null;
    }

    /// <summary>The body in <paramref name="encoding"/> (<see cref="Brotli"/> or <see cref="Gzip"/>), at the fastest level.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> body, string encoding, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var packed = new MemoryStream(Math.Max(256, body.Length / 4));

        using (Stream coder = encoding == Brotli ? new BrotliStream(packed, level, leaveOpen: true) : new GZipStream(packed, level, leaveOpen: true))
            coder.Write(body);

        return packed.ToArray();
    }

    /// <summary>
    /// Writes a finished JSON body to <paramref name="response"/>: in the coding the caller accepts
    /// (<paramref name="coded"/> supplies a body already coded, so a document served many times is coded
    /// once), with <c>Vary: Accept-Encoding</c> either way, since the answer depends on that header.
    /// </summary>
    public static async Task WriteJsonAsync(HttpResponse response, byte[] body, Func<string, byte[]>? coded = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(body);

        var encoding = EncodingFor(response.HttpContext.Request, body.Length);

        response.ContentType = "application/json; charset=utf-8";
        response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        if (encoding is not null)
        {
            body = coded?.Invoke(encoding) ?? Compress(body, encoding);
            response.Headers.ContentEncoding = encoding;
        }

        response.ContentLength = body.Length;

        await response.Body.WriteAsync(body, response.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// A 200 whose JSON body is written in the content coding the caller accepts
/// (<see cref="WireCompression"/>), serialised as every answer of the host is (the MVC JSON options).
/// </summary>
public sealed class CompressedJsonResult(object value) : IActionResult
{
    /// <summary>The answer.</summary>
    public object Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    /// <inheritdoc/>
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.HttpContext.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions ?? Core.Models.OxQLJson.Wire;

        context.HttpContext.Response.StatusCode = StatusCodes.Status200OK;

        return WireCompression.WriteJsonAsync(context.HttpContext.Response, JsonSerializer.SerializeToUtf8Bytes(Value, Value.GetType(), options));
    }
}
