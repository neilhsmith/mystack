using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

// The host is pointed at a port nothing is listening on. Everything here is about what auth does
// when its database is gone, which is the only time the two endpoints are supposed to disagree.
public sealed class UnreachableDatabaseTests : IAsyncLifetime
{
    private AuthApplicationFactory application = null!;
    private HttpClient client = null!;

    public ValueTask InitializeAsync()
    {
        application = new AuthApplicationFactory(
            AuthApplicationFactory.UnreachableConnectionString,
            migrate: false,
            AuthApplicationFactory.TestEnvironment
        );
        client = application.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await application.DisposeAsync();
    }

    [Fact]
    public async Task Live_still_returns_200_when_the_database_is_unreachable()
    {
        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_returns_503_when_the_database_is_unreachable()
    {
        var response = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var payload = await HealthPayload.ReadAsync(response);
        payload.Status.ShouldBe("Unhealthy");
        payload.Check("database").Status.ShouldBe("Unhealthy");
    }

    [Fact]
    public async Task Ready_does_not_put_the_connection_string_in_the_response()
    {
        var response = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldNotContain("127.0.0.1");
        body.ShouldNotContain("nobody");
        body.ShouldNotContain("Npgsql");
    }
}
