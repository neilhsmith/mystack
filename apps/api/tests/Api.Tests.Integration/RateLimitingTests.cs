using System.Net;
using System.Net.Http.Json;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests.Integration;

/// <summary>
/// Verifies the global per-IP rate limiter wired in <c>Program.cs</c>:
/// <list type="bullet">
///   <item>Requests above the configured limit are rejected with 429 + RFC 7807
///   <c>application/problem+json</c> (including <c>traceId</c> from the customizer)
///   and a <c>Retry-After</c> header.</item>
///   <item>Health endpoints are exempt — load balancer / k8s probes can poll freely.</item>
/// </list>
/// Uses <see cref="RateLimitedTestFactory"/> so the partition limit is small enough to
/// hit deterministically (3 per minute). Lives outside the main <c>IntegrationTestCollection</c>
/// so other test classes keep the lenient dev defaults and don't share a partition with us.
/// </summary>
public sealed class RateLimitingTests : IClassFixture<RateLimitedTestFactory>
{
    private readonly HttpClient _client;

    public RateLimitingTests(RateLimitedTestFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Exceeding_Limit_Returns429_ProblemJson_WithRetryAfter_AndTraceId()
    {
        // Burn the 3-request quota.
        for (int i = 0; i < 3; i++)
        {
            var ok = await _client.GetAsync("/v1/hello", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 4th request — over the limit, should be rejected.
        var response = await _client.GetAsync("/v1/hello", TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Health_Endpoints_Are_Exempt_Even_Far_Above_Limit()
    {
        // 10 requests > the 3-request API limit, but /health partitions to the
        // dedicated no-limiter slot and never gets rejected.
        for (int i = 0; i < 10; i++)
        {
            var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
