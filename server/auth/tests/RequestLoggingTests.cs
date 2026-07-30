using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class RequestLoggingTests(AuthAppFixture app)
{
    private const string Category = "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware";

    [Fact]
    public async Task EveryRequest_GetsOneEnvelopeLine()
    {
        var path = $"/envelope-probe-{Guid.NewGuid():N}";

        var response = await app.Client.GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var entry = app
            .Logs.Entries.Where(entry =>
                entry.Category == Category && entry.Message.Contains(path, StringComparison.Ordinal)
            )
            .ShouldHaveSingleItem();

        // The envelope, whole: method, path, status, duration.
        entry.Message.ShouldContain("GET");
        entry.Message.ShouldContain("404");
        entry.Message.ShouldContain("Duration");
    }

    [Fact]
    public async Task QueryStrings_NeverReachTheLog()
    {
        var path = $"/query-probe-{Guid.NewGuid():N}";

        await app.Client.GetAsync(
            $"{path}?token=super-secret-value",
            TestContext.Current.CancellationToken
        );

        var entries = app
            .Logs.Entries.Where(entry =>
                entry.Category == Category && entry.Message.Contains(path, StringComparison.Ordinal)
            )
            .ToList();

        // Confirm/reset tokens ride auth's query strings — the same reason span query-redaction
        // stays on for this host.
        entries.ShouldNotBeEmpty();
        entries.ShouldAllBe(entry => !entry.Message.Contains("super-secret-value"));
    }

    [Fact]
    public async Task HealthProbes_AreSuppressed()
    {
        var response = await app.Client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        app.Logs.Entries.Where(entry => entry.Category == Category)
            .ShouldAllBe(entry => !entry.Message.Contains("/health/live"));
    }
}
