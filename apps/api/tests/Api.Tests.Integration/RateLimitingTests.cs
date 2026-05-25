using System.Net;
using System.Net.Http.Json;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests.Integration;

/// <summary>
/// Verifies the global per-IP rate limiter wired in <c>Program.cs</c>:
/// requests above the configured limit are rejected with 429 + RFC 7807
/// <c>application/problem+json</c> (including <c>traceId</c> from the customizer) and
/// a <c>Retry-After</c> header.
/// <para>
/// Uses the shared <c>ApiTestFactory</c> against a dev-only diagnostic probe
/// (<c>/v1/diagnostics/rate-limit-probe</c>) that's wired to a named "diagnostic-strict"
/// policy capped at 3 requests / 60 s — strict enough to hit deterministically without
/// having to override global config or boot a second factory. (Two coexisting factories
/// would collide on the process-global env var <c>ApiTestFactory</c> uses to publish its
/// Postgres connection string; see that class's doc.)
/// </para>
/// <para>
/// Health endpoints are exempt by partition (<c>"health-exempt"</c> NoLimiter) — that
/// branch is covered by code inspection only, since proving it via test would require
/// firing more requests than the lenient dev limit (10000/min).
/// </para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class RateLimitingTests
{
    private readonly HttpClient _client;

    public RateLimitingTests(ApiTestFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Exceeding_DiagnosticStrict_Policy_Returns429_ProblemJson_WithRetryAfter_AndTraceId()
    {
        // Burn the 3-request quota on the strict-policy probe.
        for (int i = 0; i < 3; i++)
        {
            var ok = await _client.GetAsync(
                "/v1/diagnostics/rate-limit-probe", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 4th request — over the limit, should be rejected.
        var response = await _client.GetAsync(
            "/v1/diagnostics/rate-limit-probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(429, problem.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
    }
}
