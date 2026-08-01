using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class EndSessionTests(AuthAppFixture app)
{
    [Fact]
    public async Task EndSession_ConfirmsFirst_ThenSignsOutAndReturnsToTheClient()
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

        // No id_token_hint, so the GET renders the confirmation instead of acting.
        var query =
            $"?client_id={AuthAppFixture.ClientId}"
            + $"&post_logout_redirect_uri={Uri.EscapeDataString(AuthAppFixture.PostLogoutRedirectUri)}"
            + "&state=farewell";
        var confirmation = await client.GetAsync($"/connect/endsession{query}", cancellationToken);
        confirmation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await confirmation.Content.ReadAsStringAsync(cancellationToken);
        html.ShouldContain("Sign out?");
        // The request's own parameters ride the form, so the confirmed POST is the same request.
        html.ShouldContain("post_logout_redirect_uri");

        var response = await OAuth.EndSessionAsync(client, query, cancellationToken);
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

    // The forced-navigation guard: a bare GET must not end every app's session — it renders the
    // confirmation and touches nothing.
    [Fact]
    public async Task AHintlessGet_ConfirmsInsteadOfActing_AndTheSessionSurvives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"endsession-forced-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync("/connect/endsession", cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("Sign out?");

        var (_, challenge) = OAuth.CreatePkcePair();
        var authorize = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            cancellationToken
        );
        authorize.StatusCode.ShouldBe(HttpStatusCode.Found);
        authorize.Headers.Location!.ToString().ShouldStartWith(AuthAppFixture.RedirectUri);
    }

    // The cross-site half of the same guard: a POST without the confirmation form's antiforgery
    // token (and without a hint) re-renders the confirmation rather than acting.
    [Fact]
    public async Task AForgedPost_DoesNotSignOut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"endsession-csrf-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var forged = await client.PostAsync(
            "/connect/endsession",
            new FormUrlEncodedContent(new Dictionary<string, string>()),
            cancellationToken
        );
        forged.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await forged.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("Sign out?");

        var (_, challenge) = OAuth.CreatePkcePair();
        var authorize = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            cancellationToken
        );
        authorize.StatusCode.ShouldBe(HttpStatusCode.Found);
        authorize.Headers.Location!.ToString().ShouldStartWith(AuthAppFixture.RedirectUri);
    }

    [Fact]
    public async Task ABareConfirmedSignOut_LandsOnTheSignedOutPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"endsession-bare-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await OAuth.EndSessionAsync(client, cancellationToken: cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.ToString().ShouldBe("/signed-out");

        var landing = await client.GetAsync("/signed-out", cancellationToken);
        landing.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await landing.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "You've been signed out."
        );
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
