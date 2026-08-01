using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

// The pages nothing redirects a client to, but every fallback lands on: the root (the default
// post-sign-in target and end-session fallback), the signed-out confirmation, and the cookie
// handler's access-denied target. Functional here; designed in the design pass.
public sealed class LandingPageTests(AuthAppFixture app)
{
    [Fact]
    public async Task TheRoot_OffersSignIn_ToAnAnonymousVisitor()
    {
        using var client = app.CreateFlowClient();
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("/signin");
        html.ShouldContain("/register");
    }

    [Fact]
    public async Task TheRoot_ShowsTheAccount_ToASignedInVisitor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"landing-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);
        // The default post-sign-in target is the root itself — and it must exist.
        signIn.Headers.Location!.ToString().ShouldBe("/");

        var response = await client.GetAsync("/", cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        html.ShouldContain(email);
        html.ShouldContain("/connect/endsession");
        html.ShouldContain("/change-password");
    }

    [Fact]
    public async Task TheAccessDeniedPage_Exists_WhereTheCookieHandlerPoints()
    {
        using var client = app.CreateFlowClient();
        var response = await client.GetAsync(
            "/access-denied",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        ).ShouldContain("Access denied");
    }
}
