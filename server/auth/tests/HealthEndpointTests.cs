using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class HealthEndpointTests(AuthAppFixture app)
{
    [Fact]
    public async Task Live_Returns200_WithoutRunningAnyCheck()
    {
        var response = await app.Client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"status\":\"Healthy\"");
        body.ShouldContain("\"checks\":[]");
    }

    [Fact]
    public async Task Ready_Returns200_WithTheDatabaseAndSchemaChecks()
    {
        var response = await app.Client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"status\":\"Healthy\"");
        body.ShouldContain("\"name\":\"database\"");
        body.ShouldContain("\"name\":\"database-schema\"");
    }
}
