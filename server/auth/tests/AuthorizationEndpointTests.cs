using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class AuthorizationEndpointTests(AuthAppFixture app)
{
    [Fact]
    public async Task Anonymous_IsRedirectedToTheSignInPage()
    {
        using var client = app.CreateFlowClient();
        var (_, challenge) = OAuth.CreatePkcePair();

        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldContain("/signin?ReturnUrl=");
        // The round trip lands back on the authorization request it interrupted.
        location.ShouldContain(Uri.EscapeDataString("/connect/authorize"));
    }

    // prompt=none is the client promising no interaction — the answer to "nobody is signed in"
    // is the login_required error back at the client, never a sign-in page. This is what the
    // BFFs' silent-SSO checks will lean on.
    [Fact]
    public async Task PromptNone_WithNoSession_ReturnsLoginRequired()
    {
        using var client = app.CreateFlowClient();
        var (_, challenge) = OAuth.CreatePkcePair();

        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid") + "&prompt=none",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(AuthAppFixture.RedirectUri);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["error"].ShouldBe("login_required");
        query["state"].ShouldBe("xyz");
    }

    // prompt=login demands a fresh sign-in even mid-session — and the ReturnUrl the sign-in
    // page sends the user back to must have the prompt stripped, or the round trip would demand
    // a fresh sign-in forever.
    [Fact]
    public async Task PromptLogin_WithALiveSession_ReAuthenticates_WithoutLooping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"prompt-login-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (_, challenge) = OAuth.CreatePkcePair();
        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid") + "&prompt=login",
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldContain("/signin?ReturnUrl=");

        var returnUrl = Uri.UnescapeDataString(location.Split("ReturnUrl=")[1]);
        returnUrl.ShouldContain("/connect/authorize");
        returnUrl.ShouldNotContain("prompt=login");
    }

    // OIDC §11 clients send prompt=consent whenever they request offline_access. Every client
    // here is first-party with implicit consent (D17), so the consent the prompt demands is
    // already on file: the request proceeds — refresh token and all — instead of being
    // rejected for naming a prompt no screen implements (auth-track 15's conformance run).
    [Fact]
    public async Task PromptConsent_WithOfflineAccess_IsSatisfiedByImplicitConsent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"prompt-consent-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid offline_access") + "&prompt=consent",
            cancellationToken
        );

        // Straight back to the client — no interstitial page of any kind.
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(AuthAppFixture.RedirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["error"].ShouldBeNull(query["error_description"]);

        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = query["code"].ShouldNotBeNull(),
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ClientId,
                ["code_verifier"] = verifier,
            },
            cancellationToken: cancellationToken
        );

        tokens.GetProperty("refresh_token").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task APublicClient_MissingPkce_IsRejected_BeforeAnyUserInteraction()
    {
        using var client = app.CreateFlowClient();

        var url =
            $"/connect/authorize?client_id={AuthAppFixture.ClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(AuthAppFixture.RedirectUri)}"
            + "&response_type=code&scope=openid&state=xyz";

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        // A direct 400, not an error redirect: nothing travels back to the client's callback,
        // and no sign-in page was ever involved.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("invalid_request");
    }

    // The other half of the RFC 9700 split the seeder declares: PKCE is a public client's only
    // defense against code injection, so it stays mandatory above — while a confidential
    // client, whose nonce covers the same attack, completes the flow without one. The Basic OP
    // conformance profile drives exactly this PKCE-less shape.
    [Fact]
    public async Task AConfidentialClient_CompletesTheCodeFlow_WithoutPkce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"no-pkce-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();

        var url =
            $"/connect/authorize?client_id={AuthAppFixture.ConfidentialClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(AuthAppFixture.RedirectUri)}"
            + "&response_type=code&scope=openid%20email&state=xyz&nonce=n-1";

        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            url,
            cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync(url, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(AuthAppFixture.RedirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["error"].ShouldBeNull(query["error_description"]);
        var code = query["code"].ShouldNotBeNull();

        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ConfidentialClientId,
                ["client_secret"] = AuthAppFixture.ConfidentialClientSecret,
            },
            cancellationToken: cancellationToken
        );

        tokens.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        tokens.GetProperty("id_token").GetString().ShouldNotBeNullOrEmpty();
    }
}
