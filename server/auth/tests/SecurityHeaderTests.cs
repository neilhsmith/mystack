using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace MyStack.Auth.Tests;

// No database is needed to assert on headers, so these run against a host pointed at nothing.
public sealed class SecurityHeaderTests : IAsyncLifetime
{
    private AuthApplicationFactory application = null!;
    private HttpClient client = null!;

    public ValueTask InitializeAsync()
    {
        application = new AuthApplicationFactory(
            AuthApplicationFactory.UnreachableConnectionString,
            migrate: false,
            Environments.Development
        );
        client = application.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await application.DisposeAsync();
    }

    [Theory]
    // A handled request and one no endpoint matched: the headers come from middleware, so both
    // carry them. The 404 is the case that regresses when someone moves the call.
    [InlineData("/health/live")]
    [InlineData("/nothing-is-here")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var headers = response.Headers;
        headers
            .GetValues("Content-Security-Policy")
            .ShouldHaveSingleItem()
            .ShouldBe(
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
            );
        headers.GetValues("Referrer-Policy").ShouldHaveSingleItem().ShouldBe("no-referrer");
        headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        headers
            .GetValues("Cross-Origin-Opener-Policy")
            .ShouldHaveSingleItem()
            .ShouldBe("same-origin");
        headers
            .GetValues("Cross-Origin-Resource-Policy")
            .ShouldHaveSingleItem()
            .ShouldBe("same-origin");
        headers.GetValues("Permissions-Policy").ShouldHaveSingleItem().ShouldContain("camera=()");
    }

    [Fact]
    public async Task The_server_header_is_not_sent()
    {
        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_is_not_sent_in_development()
    {
        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    public async Task Hsts_is_sent_outside_development()
    {
        await using var production = new AuthApplicationFactory(
            AuthApplicationFactory.UnreachableConnectionString,
            migrate: false,
            Environments.Production
        );

        // Over https, and not to localhost: HstsOptions excludes loopback hosts by default, so a
        // test that asked for http://localhost would report "no HSTS" whatever the code did.
        using var productionClient = production.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://auth.example.test"),
            }
        );

        var response = await productionClient.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken
        );

        response
            .Headers.GetValues("Strict-Transport-Security")
            .ShouldHaveSingleItem()
            .ShouldBe("max-age=31536000; includeSubDomains");
    }
}
