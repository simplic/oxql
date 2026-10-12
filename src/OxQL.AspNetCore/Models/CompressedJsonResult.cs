using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace OxQL.AspNetCore.Models;

/// <summary>
/// The content coding of an answer a caller reads often and that compresses well (an explain answer, a
/// schema document, a page of rows): chosen from the request's <c>Accept-Encoding</c>, applied to the
/// finished body. It is done here, per answer, and not by a middleware: the host decides nothing, no
/// other route changes, and the cost is known (the fastest level; a page of 100 rows, 46 KB, is coded
/// to about 5 KB in about 0.1 ms). A caller that names no coding gets the body as it is.
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

    /// <summary>The options an answer is written with, per options a host serialises with: its own, with the wire's escaping where it names none.</summary>
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Unescaping = new();

    /// <summary>
    /// The options an answer body is written with: <paramref name="options"/> itself when it names an
    /// encoder, else a copy that escapes only what JSON requires (the quote, the backslash and the
    /// control characters), so text reaches the caller as UTF-8 and not as <c>\uXXXX</c>. It is what
    /// the framework's own JSON formatter does with options that name no encoder, and so what every
    /// answer of the host looked like before the engine wrote its bodies itself. The body is
    /// <c>application/json</c> and is sent with <c>X-Content-Type-Options: nosniff</c>; it is never
    /// part of an HTML page, which is what the default encoder's escaping of <c>&lt; &gt; &amp; ' +</c>
    /// and of every non-ASCII character protects.
    /// </summary>
    public static JsonSerializerOptions Unescaped(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Encoder is not null
            ? options
            : Unescaping.GetValue(options, static named => new JsonSerializerOptions(named) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Serialises <paramref name="value"/> and writes it to <paramref name="response"/> as
    /// <see cref="WriteJsonAsync(HttpResponse, byte[], Func{string, byte[]}?)"/> writes a finished body:
    /// in the coding the caller accepts, with <c>Vary</c> and the length. The body and its coded form
    /// are held in pooled segments, never in one array: a page of rows is often larger than what the
    /// runtime keeps out of its large object heap, and an answer must not cost a collection of it.
    /// Text is written as UTF-8, escaped only where JSON requires it (<see cref="Unescaped"/>).
    /// </summary>
    public static async Task WriteJsonAsync(HttpResponse response, object value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(value);

        ArgumentNullException.ThrowIfNull(options);

        options = Unescaped(options);

        using var body = new PooledBody();

        await using (var writer = new Utf8JsonWriter(body, new JsonWriterOptions { Encoder = options.Encoder, Indented = options.WriteIndented, SkipValidation = true }))
            JsonSerializer.Serialize(writer, value, value.GetType(), options);

        // The body is JSON whatever a client would guess from its text.
        response.Headers.XContentTypeOptions = "nosniff";

        var encoding = EncodingFor(response.HttpContext.Request, checked((int)Math.Min(body.Length, int.MaxValue)));

        response.ContentType = "application/json; charset=utf-8";
        response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        if (encoding is null)
        {
            response.ContentLength = body.Length;
            await body.CopyToAsync(response.Body, response.HttpContext.RequestAborted).ConfigureAwait(false);

            return;
        }

        using var packed = new PooledBody();

        await using (Stream coder = encoding == Brotli ? new BrotliStream(packed.AsStream(), CompressionLevel.Fastest, leaveOpen: true) : new GZipStream(packed.AsStream(), CompressionLevel.Fastest, leaveOpen: true))
            body.CopyTo(coder);

        response.Headers.ContentEncoding = encoding;
        response.ContentLength = packed.Length;

        await packed.CopyToAsync(response.Body, response.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// A body written into rented segments, each below the size the runtime allocates on its large
/// object heap, and returned to the pool on disposal.
/// </summary>
internal sealed class PooledBody : IBufferWriter<byte>, IDisposable
{
    /// <summary>The size of a segment: under the 85 000 bytes from which an array is a large object.</summary>
    private const int SegmentBytes = 64 * 1024;

    private readonly List<(byte[] Buffer, int Written)> full = [];
    private byte[]? current;
    private int written;

    /// <summary>The bytes written so far.</summary>
    public long Length { get; private set; }

    /// <inheritdoc/>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (current is null || count > current.Length - written)
            throw new InvalidOperationException("More was written than the segment asked for holds.");

        written += count;
        Length += count;
    }

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0) => Segment(sizeHint).AsMemory(written);

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0) => Segment(sizeHint).AsSpan(written);

    private byte[] Segment(int sizeHint)
    {
        var needed = Math.Max(1, sizeHint);

        if (current is not null && current.Length - written >= needed)
            return current;

        if (current is not null)
            full.Add((current, written));

        current = ArrayPool<byte>.Shared.Rent(Math.Max(SegmentBytes, needed));
        written = 0;

        return current;
    }

    private IEnumerable<ReadOnlyMemory<byte>> Segments()
    {
        foreach (var (buffer, count) in full)
            yield return buffer.AsMemory(0, count);

        if (current is not null && written > 0)
            yield return current.AsMemory(0, written);
    }

    /// <summary>Writes the body to <paramref name="target"/>.</summary>
    public async Task CopyToAsync(Stream target, CancellationToken cancellationToken)
    {
        foreach (var segment in Segments())
            await target.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the body to <paramref name="target"/>.</summary>
    public void CopyTo(Stream target)
    {
        foreach (var segment in Segments())
            target.Write(segment.Span);
    }

    /// <summary>A write-only stream over this body, for a coder to write into.</summary>
    public Stream AsStream() => new Writer(this);

    public void Dispose()
    {
        foreach (var (buffer, _) in full)
            ArrayPool<byte>.Shared.Return(buffer);

        if (current is not null)
            ArrayPool<byte>.Shared.Return(current);

        full.Clear();
        current = null;
    }

    private sealed class Writer(PooledBody body) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => body.Length;

        public override long Position { get => body.Length; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var span = body.GetSpan();
                var take = Math.Min(span.Length, buffer.Length);

                buffer[..take].CopyTo(span);
                body.Advance(take);
                buffer = buffer[take..];
            }
        }
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

        return WireCompression.WriteJsonAsync(context.HttpContext.Response, Value, options);
    }
}
