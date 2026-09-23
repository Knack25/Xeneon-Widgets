using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

internal interface IBufferedHttpResult
{
    Task<BufferedHttpResponse> PrepareAsync(HttpContext http);
}

internal sealed class BufferedHttpResponse
{
    private readonly byte[] body;
    private readonly KeyValuePair<string, string[]>[] headers;

    private BufferedHttpResponse(int statusCode, byte[] body, KeyValuePair<string, string[]>[] headers)
    {
        StatusCode = statusCode;
        this.body = body;
        this.headers = headers;
    }

    public int StatusCode { get; }

    public static async Task<BufferedHttpResponse> CreateAsync(IResult inner, HttpContext source,
        long maximumBytes, CancellationToken cancellationToken)
    {
        await using var buffer = new CappedMemoryStream(maximumBytes);
        var context = new DefaultHttpContext
        {
            RequestServices = source.RequestServices,
            RequestAborted = cancellationToken,
            TraceIdentifier = source.TraceIdentifier
        };
        context.Request.Scheme = source.Request.Scheme;
        context.Request.Host = source.Request.Host;
        context.Request.PathBase = source.Request.PathBase;
        context.Request.Path = source.Request.Path;
        context.Request.QueryString = source.Request.QueryString;
        context.Response.Body = buffer;

        await inner.ExecuteAsync(context);
        var headers = context.Response.Headers
            .Where(header => !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(header => new KeyValuePair<string, string[]>(header.Key,
                header.Value.Select(value => value ?? string.Empty).ToArray()))
            .ToArray();
        return new BufferedHttpResponse(context.Response.StatusCode, buffer.ToArray(), headers);
    }

    public async Task CopyToAsync(HttpContext target)
    {
        target.Response.StatusCode = StatusCode;
        foreach (var header in headers) target.Response.Headers[header.Key] = header.Value;
        target.Response.ContentLength = body.Length;
        await target.Response.Body.WriteAsync(body, target.RequestAborted);
    }

    private sealed class CappedMemoryStream(long maximumBytes) : MemoryStream
    {
        private void RequireCapacity(long bytes)
        {
            if (bytes < 0 || Position > maximumBytes - bytes)
                throw new OutlookException("response_too_large",
                    "The response is too large to publish safely.",
                    StatusCodes.Status503ServiceUnavailable);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            RequireCapacity(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            RequireCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            RequireCapacity(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value > maximumBytes)
                throw new OutlookException("response_too_large",
                    "The response is too large to publish safely.",
                    StatusCodes.Status503ServiceUnavailable);
            base.SetLength(value);
        }
    }
}
