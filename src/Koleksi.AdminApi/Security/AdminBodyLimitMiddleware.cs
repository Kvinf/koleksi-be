namespace Koleksi.AdminApi.Security;

/// <summary>
/// Refuses a request body larger than <see cref="MaxBytes"/> before anything reads it.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel's own limit is not what protects a Lambda: API Gateway hands the whole body to the function as an event, so the
/// framework never sees a socket to cut off. Without a cap of its own, the only bound was the gateway's 10 MB, and a request that
/// large was read, parsed and only then refused by the validation that follows. Nothing staff send is anywhere near that
/// size (the largest body is a reason of a thousand characters), so a small cap costs nobody anything and ends an easy way to waste
/// a Lambda's time and memory.
/// </para>
/// <para>
/// A declared <c>Content-Length</c> over the cap is answered 413 at once. A body with no declared length is counted as it is read
/// and cut off at the cap with <see cref="AdminBodyTooLargeException"/>, which the exception handler also answers 413.
/// </para>
/// </remarks>
public sealed class AdminBodyLimitMiddleware(RequestDelegate next)
{
    /// <summary>64 KiB.</summary>
    public const long MaxBytes = 64 * 1024;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength is { } declared)
        {
            return declared > MaxBytes ? RefuseAsync(context) : next(context);
        }

        if (context.Request.Body is not null && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Request.Body = new AdminLimitedReadStream(context.Request.Body, MaxBytes);
        }

        return next(context);
    }

    private static Task RefuseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return context.Response.WriteAsJsonAsync(new { status = 413, title = "Request body too large", code = AdminBodyTooLargeException.Code });
    }
}

/// <summary>Thrown while reading a body that has no declared length and has grown past the cap.</summary>
public sealed class AdminBodyTooLargeException()
    : BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge)
{
    public const string Code = "request.too_large";
}

/// <summary>A read-only view of a request body that stops at a byte limit.</summary>
internal sealed class AdminLimitedReadStream(Stream inner, long limit) : Stream
{
    private long read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int bytes)
    {
        read += bytes;
        return read > limit ? throw new AdminBodyTooLargeException() : bytes;
    }
}
