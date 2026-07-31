using System.Net;
using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

// The endpoint is OpenIddict's entirely — these tests pin the posture: truthful answers for an
// authenticated confidential caller about its own tokens, active:false for everything else, and
// no access at all for public clients.
public sealed class IntrospectionTests(AuthAppFixture app)
{
    [Fact]
    public async Task OwnToken_IsActive_WithItsShape()
    {
        var token = await MachineTokenAsync(TestContext.Current.CancellationToken);

        var body = await IntrospectAsync(
            token,
            AuthAppFixture.MachineClientId,
            AuthAppFixture.MachineClientSecret,
            cancellationToken: TestContext.Current.CancellationToken
        );

        body.GetProperty("active").GetBoolean().ShouldBeTrue();
        body.GetProperty("client_id").GetString().ShouldBe(AuthAppFixture.MachineClientId);
        body.GetProperty("sub").GetString().ShouldBe(AuthAppFixture.MachineClientId);
        body.GetProperty("aud").GetString().ShouldBe("api");
        body.GetProperty("exp").GetInt64().ShouldBeGreaterThan(0);

        // Deliberately absent: OpenIddict releases a token's claim details — scope included —
        // to its audiences only. This caller is merely the presenter, so it gets liveness and
        // metadata, nothing confidential.
        body.TryGetProperty("scope", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GarbageToken_IsInactive_AndNothingElse()
    {
        var body = await IntrospectAsync(
            "not-a-token-at-all",
            AuthAppFixture.MachineClientId,
            AuthAppFixture.MachineClientSecret,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // RFC 7662 §2.2: an inactive answer is `active: false` and nothing more — anything
        // else would leak why.
        body.GetProperty("active").GetBoolean().ShouldBeFalse();
        body.EnumerateObject().Count().ShouldBe(1);
    }

    [Fact]
    public async Task SomeoneElsesToken_IsInactive()
    {
        var email = $"introspect-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: TestContext.Current.CancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            client,
            challenge,
            "openid api.read",
            TestContext.Current.CancellationToken
        );
        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ClientId,
                ["code_verifier"] = verifier,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        // A perfectly valid user token — but the machine client neither presented it nor is an
        // audience of it, so the truthful answer to *this caller* is inactive: introspection
        // must not become a stolen-token probe.
        var body = await IntrospectAsync(
            tokens.GetProperty("access_token").GetString()!,
            AuthAppFixture.MachineClientId,
            AuthAppFixture.MachineClientSecret,
            cancellationToken: TestContext.Current.CancellationToken
        );

        body.GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PublicClient_IsRefused_EvenWithAValidToken()
    {
        var token = await MachineTokenAsync(TestContext.Current.CancellationToken);

        var body = await IntrospectAsync(
            token,
            AuthAppFixture.ClientId,
            secret: null,
            HttpStatusCode.BadRequest,
            TestContext.Current.CancellationToken
        );

        body.GetProperty("error").GetString().ShouldBe("unauthorized_client");
    }

    // The future BFF's exact shape, end to end: a confidential browser client completes the
    // code flow presenting its secret at the token endpoint, then introspects the user token it
    // was issued — liveness without holding auth's key material.
    [Fact]
    public async Task ConfidentialClient_CompletesTheCodeFlow_AndIntrospectsItsOwnToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"confidential-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            client,
            challenge,
            "openid email api.read",
            cancellationToken,
            AuthAppFixture.ConfidentialClientId
        );

        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ConfidentialClientId,
                ["client_secret"] = AuthAppFixture.ConfidentialClientSecret,
                ["code_verifier"] = verifier,
            },
            cancellationToken: cancellationToken
        );

        var body = await IntrospectAsync(
            tokens.GetProperty("access_token").GetString()!,
            AuthAppFixture.ConfidentialClientId,
            AuthAppFixture.ConfidentialClientSecret,
            cancellationToken: cancellationToken
        );

        body.GetProperty("active").GetBoolean().ShouldBeTrue();
        body.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        body.GetProperty("client_id").GetString().ShouldBe(AuthAppFixture.ConfidentialClientId);
    }

    private async Task<string> MachineTokenAsync(CancellationToken cancellationToken)
    {
        var body = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AuthAppFixture.MachineClientId,
                ["client_secret"] = AuthAppFixture.MachineClientSecret,
                ["scope"] = "api.read",
            },
            cancellationToken: cancellationToken
        );

        return body.GetProperty("access_token").GetString()!;
    }

    private async Task<JsonElement> IntrospectAsync(
        string token,
        string clientId,
        string? secret,
        HttpStatusCode expected = HttpStatusCode.OK,
        CancellationToken cancellationToken = default
    )
    {
        var form = new Dictionary<string, string> { ["token"] = token, ["client_id"] = clientId };
        if (secret is not null)
        {
            form["client_secret"] = secret;
        }

        var response = await app.Client.PostAsync(
            "/connect/introspection",
            new FormUrlEncodedContent(form),
            cancellationToken
        );

        response.StatusCode.ShouldBe(expected);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
