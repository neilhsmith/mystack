using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Api.Http;

/// <summary>
/// RFC 7232 precondition handling for endpoints that surface an ETag.
/// <para>
/// Two flows, matching the two ways clients use ETags:
/// </para>
/// <list type="bullet">
///   <item><see cref="EvaluateRead"/> — read path. Always sets the <c>ETag</c> response header;
///   returns a 304 result when <c>If-None-Match</c> indicates the client's copy is current.</item>
///   <item><see cref="EvaluateWrite"/> — write path. Demands <c>If-Match</c> (428 if absent,
///   412 if present but doesn't match). Returns null when the caller may proceed with the
///   write; sets the current <c>ETag</c> on the 412 response so the client can recover.</item>
/// </list>
/// <para>
/// Return types are <see cref="StatusCodeHttpResult"/> / <see cref="ProblemHttpResult"/>
/// rather than the untyped <see cref="IResult"/> so calling handlers can include them in
/// their <c>Results&lt;...&gt;</c> return union — that's what lets the OpenAPI generator
/// see the response shapes.
/// </para>
/// </summary>
public static class ConditionalRequest
{
    /// <summary>
    /// Read-side conditional handling. Always writes the <c>ETag</c> response header.
    /// Returns a <c>304 Not Modified</c> result when the request's <c>If-None-Match</c>
    /// indicates the client already holds the current representation; otherwise <c>null</c>
    /// (caller serves the full body).
    /// </summary>
    public static StatusCodeHttpResult? EvaluateRead(HttpContext context, EntityTagHeaderValue etag)
    {
        SetETagHeader(context, etag);

        var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Count == 0)
        {
            return null;
        }

        foreach (var candidate in ifNoneMatch)
        {
            if (Matches(candidate, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        return null;
    }

    /// <summary>
    /// Write-side conditional handling. Enforces that the client supplied an <c>If-Match</c>
    /// header and that it matches the resource's current ETag.
    /// <list type="bullet">
    ///   <item>Missing <c>If-Match</c> → <c>428 Precondition Required</c>.</item>
    ///   <item>Present but no candidate matches → <c>412 Precondition Failed</c> (with the
    ///   current <c>ETag</c> on the response so the client can re-fetch and retry).</item>
    ///   <item>Match → <c>null</c>; caller proceeds with the write.</item>
    /// </list>
    /// </summary>
    public static ProblemHttpResult? EvaluateWrite(HttpContext context, EntityTagHeaderValue etag)
    {
        var ifMatch = context.Request.GetTypedHeaders().IfMatch;

        if (ifMatch.Count == 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "If-Match header is required for this operation.");
        }

        foreach (var candidate in ifMatch)
        {
            if (Matches(candidate, etag))
            {
                return null;
            }
        }

        SetETagHeader(context, etag);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "If-Match does not match the current resource ETag.");
    }

    public static void SetETagHeader(HttpContext context, EntityTagHeaderValue etag) =>
        context.Response.Headers.ETag = etag.ToString();

    private static bool Matches(EntityTagHeaderValue candidate, EntityTagHeaderValue current)
    {
        // `*` matches any existing resource (RFC 7232 §3.1 and §3.3).
        if (candidate.Tag == "*")
        {
            return true;
        }

        // Strong comparison: tags equal AND neither is weak. Weak comparison would also
        // accept matching weak tags, but ETags issued by this app are always strong, and
        // weak validators are explicitly forbidden for If-Match (RFC 7232 §3.1).
        return candidate.Tag == current.Tag && !candidate.IsWeak && !current.IsWeak;
    }
}
