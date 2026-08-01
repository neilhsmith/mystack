using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

// The limiter counts requests, not outcomes, and sits in front of authentication and
// antiforgery — so hammering here uses the cheapest request that reaches the endpoint (an empty
// POST, an anonymous GET) and still proves the guard. Partitions are per path and per caller
// address; every flow client carries its own address, which is what the bystander checks lean
// on.
public sealed class RateLimitingTests(AuthAppFixture app)
{
    // Mirrors RateLimitOptions' defaults: Testing runs the production limits.
    public static TheoryData<string, string, int> GuardedEndpoints =>
        new()
        {
            { "/signin", "POST", 10 },
            { "/register", "POST", 5 },
            { "/forgot-password", "POST", 5 },
            { "/resend-confirmation", "POST", 5 },
            { "/change-password", "POST", 10 },
            { "/connect/verify", "GET", 10 },
        };

    [Theory]
    [MemberData(nameof(GuardedEndpoints))]
    public async Task Hammering_Trips429_WhileAnotherCallerStaysUntouched(
        string path,
        string method,
        int limit
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hammer = app.CreateFlowClient();

        for (var sent = 1; sent <= limit; sent++)
        {
            var allowed = await SendAsync(hammer, method, path, cancellationToken);
            ((int)allowed.StatusCode).ShouldNotBe(
                429,
                $"request {sent} of {limit} should still be within the window"
            );
        }

        var rejected = await SendAsync(hammer, method, path, cancellationToken);
        ((int)rejected.StatusCode).ShouldBe(429);
        rejected.Headers.GetValues("Retry-After").ShouldHaveSingleItem().ShouldBe("60");
        rejected.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        // A different caller against the same endpoint, inside the same window: untouched.
        using var bystander = app.CreateFlowClient();
        var stillAllowed = await SendAsync(bystander, method, path, cancellationToken);
        ((int)stillAllowed.StatusCode).ShouldNotBe(429);
    }

    // The full ordinary flow, not just a status probe: while one caller is limited out of the
    // sign-in POST, another signs in end to end.
    [Fact]
    public async Task AHammeredSignIn_DoesNotSlowAnybodyElseDown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"bystander-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var hammer = app.CreateFlowClient();
        for (var sent = 0; sent <= 10; sent++)
        {
            await SendAsync(hammer, "POST", "/signin", cancellationToken);
        }
        ((int)(await SendAsync(hammer, "POST", "/signin", cancellationToken)).StatusCode).ShouldBe(
            429
        );

        using var bystander = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            bystander,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    // A browser hitting the limit gets the error page, not a bare status or a JSON body.
    [Fact]
    public async Task ABrowserOverTheLimit_GetsTheErrorPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hammer = app.CreateFlowClient();
        hammer.DefaultRequestHeaders.Add("Accept", "text/html");

        HttpResponseMessage response = null!;
        for (var sent = 0; sent <= 10; sent++)
        {
            response = await SendAsync(hammer, "GET", "/connect/verify", cancellationToken);
        }

        ((int)response.StatusCode).ShouldBe(429);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Too many requests"
        );
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string method,
        string path,
        CancellationToken cancellationToken
    ) =>
        method == "GET"
            ? client.GetAsync(path, cancellationToken)
            : client.PostAsync(
                path,
                new FormUrlEncodedContent(new Dictionary<string, string>()),
                cancellationToken
            );
}
