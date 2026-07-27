using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class HealthEndpointTests(AuthAppFixture app)
{
    [Fact]
    public async Task Live_reports_healthy_without_running_a_single_check()
    {
        var response = await app.Client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await HealthPayload.ReadAsync(response);
        payload.Status.ShouldBe("Healthy");
        payload.Checks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ready_reports_the_database_and_the_schema()
    {
        var response = await app.Client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await HealthPayload.ReadAsync(response);
        payload.Status.ShouldBe("Healthy");
        payload.Check("database").Status.ShouldBe("Healthy");
        payload.Check("database-schema").Status.ShouldBe("Healthy");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_responses_are_json_and_never_cached(string path)
    {
        var response = await app.Client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
    }
}
