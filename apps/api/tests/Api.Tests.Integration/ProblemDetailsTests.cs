using System.Net;
using System.Net.Http.Json;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests.Integration;

/// <summary>
/// Cross-cutting guard for the RFC 7807 / 9457 error pipeline:
/// <list type="bullet">
///   <item>Unhandled exceptions go through <c>UseExceptionHandler</c> and come back as
///   <c>application/problem+json</c> with no stack trace leak.</item>
///   <item>The <c>AddProblemDetails</c> customizer attaches <c>traceId</c> to every
///   problem response — including ones the handler builds directly via
///   <c>Results.Problem</c> (412 / 428 in PostsEndpoints).</item>
/// </list>
/// Lives in its own file because it tests cross-cutting middleware, not a single resource.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProblemDetailsTests
{
    private readonly HttpClient _client;

    public ProblemDetailsTests(ApiTestFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task UnhandledException_Returns500_AsProblemJson_WithTraceId()
    {
        // The /v1/diagnostics/throw probe is registered only in Development (which the
        // test factory runs as). It throws InvalidOperationException; the exception
        // handler middleware turns it into problem+json.
        var response = await _client.GetAsync("/v1/diagnostics/throw", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(500, problem.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));

        // Belt-and-braces: the exception message must NOT appear in the body. Production
        // safety check — we don't leak internals just because we have a structured shape.
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Deliberate exception", raw);
    }

    [Fact]
    public async Task HandlerProblemResult_AlsoCarriesTraceId()
    {
        // A handler-built ProblemHttpResult (here: PUT without If-Match → 428) must
        // also go through the ProblemDetails customizer, not bypass it. This is what
        // proves the customizer wires through Results.Problem, not just UseExceptionHandler.
        var created = await CreatePost("trace-id-probe", "body");

        var response = await _client.PutAsJsonAsync(
            $"/v1/posts/{created.Id}",
            new UpdatePostRequest("updated", "body"),
            TestContext.Current.CancellationToken);

        Assert.Equal(428, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
    }

    private async Task<PostResponse> CreatePost(string title, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/posts",
            new CreatePostRequest(title, content),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken))!;
    }
}
