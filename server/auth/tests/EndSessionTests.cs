using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class EndSessionTests(AuthAppFixture app)
{
    [Fact]
    public async Task EndSession_SignsOutAndReturnsToTheClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"endsession-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync(
            $"/connect/endsession?client_id={AuthAppFixture.ClientId}"
                + $"&post_logout_redirect_uri={Uri.EscapeDataString(AuthAppFixture.PostLogoutRedirectUri)}"
                + "&state=farewell",
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.ToString().ShouldStartWith(AuthAppFixture.PostLogoutRedirectUri);

        // The cookie is genuinely gone: the next authorization request has to sign in again.
        var (_, challenge) = OAuth.CreatePkcePair();
        var authorize = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            cancellationToken
        );
        authorize.StatusCode.ShouldBe(HttpStatusCode.Found);
        authorize.Headers.Location!.ToString().ShouldContain("/signin");
    }

    // The open-redirect refusal: a post_logout_redirect_uri the client never registered is not
    // followed — and the whole request is rejected before the passthrough, so the session
    // survives too. An attacker-crafted sign-out link can neither phish nor sign out.
    [Fact]
    public async Task AnUnregisteredPostLogoutRedirect_IsRefused_AndTheSessionSurvives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"endsession-evil-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync(
            $"/connect/endsession?client_id={AuthAppFixture.ClientId}"
                + $"&post_logout_redirect_uri={Uri.EscapeDataString("https://evil.example/phish")}",
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();

        // The rejection happened before the sign-out passthrough: still signed in.
        var (_, challenge) = OAuth.CreatePkcePair();
        var authorize = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            cancellationToken
        );
        authorize.StatusCode.ShouldBe(HttpStatusCode.Found);
        authorize.Headers.Location!.ToString().ShouldStartWith(AuthAppFixture.RedirectUri);
    }
}
