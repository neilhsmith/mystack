using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Http;

/// <summary>
/// Runtime guardrail registered alongside the <c>.WithEtagResponseHeader()</c> /
/// <c>.WithConditionalRead()</c> / <c>.WithConditionalWrite()</c> markers. After the
/// handler runs, asserts that the response carries an <c>ETag</c> header on every status
/// code the OpenAPI spec advertises a tag for — same status/kind table the operation
/// transformer uses, so the spec and the runtime can't disagree silently.
/// <para>
/// Forgetting <see cref="ConditionalRequest.SetETagHeader"/> on a successful write was
/// the easiest way for the spec and the runtime to drift. With this filter wired in,
/// that drift surfaces as an <see cref="InvalidOperationException"/> on the request that
/// did the forgetting — caught by the integration tests in CI, not by a downstream
/// client wondering why their cache always misses.
/// </para>
/// </summary>
internal sealed class ConditionalRequestETagAssertionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        var marker = context.HttpContext.GetEndpoint()?
            .Metadata.GetMetadata<ConditionalRequestMarker>();
        if (marker is null)
        {
            return result;
        }

        var statusCode = ExtractStatusCode(result);
        if (statusCode is null || !PromisesETag(marker.Kind, statusCode.Value))
        {
            return result;
        }

        if (!context.HttpContext.Response.Headers.ContainsKey("ETag"))
        {
            throw new InvalidOperationException(
                $"Endpoint with {nameof(ConditionalRequestMarker)}({marker.Kind}) returned " +
                $"HTTP {statusCode.Value} but did not set an ETag response header. The OpenAPI " +
                $"contract advertises one — make the handler call " +
                $"ConditionalRequest.SetETagHeader(http, ETag.From(db, entity)) before returning, " +
                $"or use ConditionalRequest.EvaluateRead/EvaluateWrite which set it for you.");
        }

        return result;
    }

    /// <summary>
    /// Peel <c>Results&lt;...&gt;</c> wrappers (which expose the active inner result via
    /// <see cref="INestedHttpResult"/>) until we reach an <see cref="IStatusCodeHttpResult"/>.
    /// Returns null if we hit something that doesn't carry a status code — the filter then
    /// skips silently rather than guessing.
    /// </summary>
    private static int? ExtractStatusCode(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }
        return (result as IStatusCodeHttpResult)?.StatusCode;
    }

    /// <summary>
    /// Mirror of <c>ConditionalRequestOperationTransformer</c>'s per-kind status table.
    /// If these ever drift, the spec will say one thing and this filter will enforce
    /// another — keep them in lockstep.
    /// </summary>
    private static bool PromisesETag(ConditionalRequestKind kind, int statusCode) =>
        (kind, statusCode) switch
        {
            (ConditionalRequestKind.EtagResponse, 200 or 201) => true,
            (ConditionalRequestKind.Read, 200 or 304) => true,
            (ConditionalRequestKind.Write, 200 or 412) => true,
            _ => false,
        };
}
