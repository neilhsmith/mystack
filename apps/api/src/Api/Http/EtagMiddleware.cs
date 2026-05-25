using System.Security.Cryptography;

namespace Api.Http;

/// <summary>
/// Body-hash <c>ETag</c> middleware for safe (idempotent) reads — applies to every
/// <c>GET</c> outside the <c>/health*</c> probe paths. Buffers the response, computes a
/// strong SHA1 hex tag over the body, sets the <c>ETag</c> response header, and
/// short-circuits to <c>304 Not Modified</c> when <c>If-None-Match</c> matches.
/// <para>
/// Endpoints stay ignorant: no ETag computation per handler, no per-route opt-in. The
/// tradeoff is intentional — this tag has no relationship to the DB row version, so it
/// cannot back <c>If-Match</c> preconditions for safe writes. Lost-update protection
/// instead relies on the <c>xmin</c> EF concurrency token applied in
/// <c>AppDbContext.OnModelCreating</c>, surfaced via the
/// <see cref="DbUpdateConcurrencyExceptionHandler"/> as a <c>409 Conflict</c>.
/// </para>
/// </summary>
public sealed class EtagMiddleware
{
    private readonly RequestDelegate _next;

    public EtagMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // ETag/304 only helps idempotent, cacheable reads. Writes are pass-through.
        // Health probes are exempt for the same reason they're exempt from rate-limiting —
        // ops infra shouldn't get tangled in response-shaping concerns.
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;

        // Only hash 200 OK bodies. Other statuses (204, 3xx, 4xx, 5xx) pass through as-is —
        // we don't want a body-hash tag on a problem+json error response, and 304 itself
        // never has a body to hash.
        if (context.Response.StatusCode != StatusCodes.Status200OK || buffer.Length == 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
            return;
        }

        // Respect any ETag the handler already set (rare; nothing in the API does today,
        // but a future endpoint with a domain-meaningful tag shouldn't have it overwritten).
        var etag = context.Response.Headers.ETag.ToString();
        if (string.IsNullOrEmpty(etag))
        {
            var hash = SHA1.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            etag = $"\"{Convert.ToHexString(hash)}\"";
            context.Response.Headers.ETag = etag;
        }

        foreach (var candidate in context.Request.Headers.IfNoneMatch)
        {
            if (candidate == "*" || candidate == etag)
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = 0;
                // Per RFC 9110 §15.4.5, 304 carries no message body.
                return;
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
    }
}
