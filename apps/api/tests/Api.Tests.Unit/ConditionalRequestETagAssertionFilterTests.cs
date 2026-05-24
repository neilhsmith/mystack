using Api.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Tests.Unit;

/// <summary>
/// Covers <see cref="ConditionalRequestETagAssertionFilter"/>'s decision matrix:
/// for each <see cref="ConditionalRequestKind"/>, exactly the status codes the OpenAPI
/// transformer advertises an ETag for must throw when the header is missing, and any
/// other status code must pass through silently. Also covers the no-marker pass-through
/// path and the <see cref="INestedHttpResult"/> unwrap used for <c>Results&lt;...&gt;</c>
/// union returns.
/// </summary>
public class ConditionalRequestETagAssertionFilterTests
{
    // ---------- The promise matrix ----------

    [Theory]
    [InlineData(ConditionalRequestKind.EtagResponse, 200)]
    [InlineData(ConditionalRequestKind.EtagResponse, 201)]
    [InlineData(ConditionalRequestKind.Read, 200)]
    [InlineData(ConditionalRequestKind.Read, 304)]
    [InlineData(ConditionalRequestKind.Write, 200)]
    [InlineData(ConditionalRequestKind.Write, 412)]
    public async Task Throws_WhenStatusPromisesETag_AndHeaderMissing(
        ConditionalRequestKind kind, int statusCode)
    {
        var context = ContextFor(kind);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Filter().InvokeAsync(context, _ => StatusCode(statusCode)).AsTask());

        Assert.Contains($"HTTP {statusCode}", ex.Message);
        Assert.Contains(kind.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(ConditionalRequestKind.EtagResponse, 200)]
    [InlineData(ConditionalRequestKind.EtagResponse, 201)]
    [InlineData(ConditionalRequestKind.Read, 200)]
    [InlineData(ConditionalRequestKind.Read, 304)]
    [InlineData(ConditionalRequestKind.Write, 200)]
    [InlineData(ConditionalRequestKind.Write, 412)]
    public async Task Passes_WhenStatusPromisesETag_AndHeaderPresent(
        ConditionalRequestKind kind, int statusCode)
    {
        var context = ContextFor(kind);
        context.HttpContext.Response.Headers.ETag = "\"deadbeef\"";

        var result = await Filter().InvokeAsync(context, _ => StatusCode(statusCode));

        Assert.NotNull(result);
    }

    // Status codes the matrix says do NOT promise an ETag — must pass through even
    // without one (e.g. DELETE's 204, GET's 404, write's 428).

    [Theory]
    [InlineData(ConditionalRequestKind.EtagResponse, 204)]
    [InlineData(ConditionalRequestKind.EtagResponse, 400)]
    [InlineData(ConditionalRequestKind.Read, 404)]
    [InlineData(ConditionalRequestKind.Write, 204)]  // DELETE success
    [InlineData(ConditionalRequestKind.Write, 404)]
    [InlineData(ConditionalRequestKind.Write, 428)]
    public async Task Passes_WhenStatusDoesNotPromiseETag_EvenWithoutHeader(
        ConditionalRequestKind kind, int statusCode)
    {
        var context = ContextFor(kind);

        var result = await Filter().InvokeAsync(context, _ => StatusCode(statusCode));

        Assert.NotNull(result);
    }

    // ---------- No marker → no enforcement ----------

    [Fact]
    public async Task Passes_WhenNoMarkerPresent_RegardlessOfHeader()
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "test"));
        var context = new DefaultEndpointFilterInvocationContext(http);

        // 200 without ETag — would throw with a marker, but no marker means no enforcement.
        var result = await Filter().InvokeAsync(context, _ => StatusCode(200));

        Assert.NotNull(result);
    }

    // ---------- Results<...> unwrap path ----------

    [Fact]
    public async Task Throws_When_ResultsUnion_Unwraps_To_Ok_Without_ETag()
    {
        // Handler signature in the wild: Task<Results<Ok<T>, NotFound, ProblemHttpResult>>.
        // Filter must peel the union via INestedHttpResult to see the actual 200.
        var context = ContextFor(ConditionalRequestKind.Write);

        Results<Ok<string>, NotFound> union = TypedResults.Ok("body");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Filter().InvokeAsync(context, _ => ValueTask.FromResult<object?>(union)).AsTask());
    }

    [Fact]
    public async Task Passes_When_ResultsUnion_Unwraps_To_StatusNotPromisingETag()
    {
        var context = ContextFor(ConditionalRequestKind.Write);

        Results<Ok<string>, NotFound> union = TypedResults.NotFound();

        var result = await Filter().InvokeAsync(context, _ => ValueTask.FromResult<object?>(union));

        Assert.NotNull(result);
    }

    // ---------- helpers ----------

    private static ConditionalRequestETagAssertionFilter Filter() => new();

    private static DefaultEndpointFilterInvocationContext ContextFor(ConditionalRequestKind kind)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(
            requestDelegate: _ => Task.CompletedTask,
            metadata: new EndpointMetadataCollection(new ConditionalRequestMarker(kind)),
            displayName: "test"));
        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static ValueTask<object?> StatusCode(int code) => code switch
    {
        200 => ValueTask.FromResult<object?>(TypedResults.Ok("body")),
        201 => ValueTask.FromResult<object?>(TypedResults.Created("/x", "body")),
        204 => ValueTask.FromResult<object?>(TypedResults.NoContent()),
        304 => ValueTask.FromResult<object?>(TypedResults.StatusCode(304)),
        400 => ValueTask.FromResult<object?>(TypedResults.BadRequest()),
        404 => ValueTask.FromResult<object?>(TypedResults.NotFound()),
        412 => ValueTask.FromResult<object?>(TypedResults.Problem(statusCode: 412)),
        428 => ValueTask.FromResult<object?>(TypedResults.Problem(statusCode: 428)),
        _ => ValueTask.FromResult<object?>(TypedResults.StatusCode(code)),
    };
}
