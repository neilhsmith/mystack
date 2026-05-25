using System.Net;
using System.Net.Http.Json;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests.Integration;

/// <summary>
/// Cross-cutting guard for the RFC 7807 / 9457 error pipeline:
/// <list type="bullet">
///   <item>Unhandled exceptions go through <c>UseExceptionHandler</c> and come back as
///   <c>application/problem+json</c> with no stack trace leak.</item>
///   <item><c>DbUpdateConcurrencyException</c> is intercepted by
///   <c>DbUpdateConcurrencyExceptionHandler</c> (an <c>IExceptionHandler</c>) and surfaces
///   as a <c>409 Conflict</c> problem+json — the dedicated path for lost-update races
///   thrown by EF.</item>
///   <item>The <c>AddProblemDetails</c> customizer attaches <c>traceId</c> to every
///   problem response so a client report can be matched against server logs.</item>
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
    public async Task DbUpdateConcurrencyException_Maps_To_409_ProblemJson_WithTraceId()
    {
        // The /v1/diagnostics/throw-concurrency probe throws DbUpdateConcurrencyException
        // directly (no real EF race needed) so we can assert the IExceptionHandler mapping
        // in isolation. The DB-level race that actually triggers this exception is
        // exercised end-to-end by XminConcurrencyDbSafetyNetTests.
        var response = await _client.GetAsync(
            "/v1/diagnostics/throw-concurrency", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
    }
}
