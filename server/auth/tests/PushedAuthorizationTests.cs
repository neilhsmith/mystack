using System.Net;
using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

// PAR (RFC 9126): authorize parameters travel the back channel and the browser carries only a
// one-time request_uri handle — plus the per-client opt-in that makes the back channel mandatory.
public sealed class PushedAuthorizationTests(AuthAppFixture app)
{
    [Fact]
    public async Task ParRoundTrip_DrivesTheCodeFlowOffTheRequestUri()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, user) = await SignedInClientAsync(cancellationToken);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var push = await PushAsync(
            client,
            AuthAppFixture.ClientId,
            challenge,
            "openid email api.read offline_access",
            cancellationToken
        );

        var requestUri = push.GetProperty("request_uri").GetString()!;
        requestUri.ShouldStartWith("urn:ietf:params:oauth:request_uri:");
        push.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);

        // The front channel now carries nothing but the client and the handle — every pushed
        // parameter, state included, must come back as if it had been in the URL.
        var code = await AuthorizeByRequestUriAsync(
            client,
            AuthAppFixture.ClientId,
            requestUri,
            cancellationToken
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
            cancellationToken: cancellationToken
        );

        var payload = OAuth.DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!);
        payload.GetProperty("sub").GetString().ShouldBe(user);
        payload.GetProperty("scope").GetString()!.ShouldContain("api.read");
    }

    [Fact]
    public async Task ARequestUri_IsSingleUse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, _) = await SignedInClientAsync(cancellationToken);

        var (_, challenge) = OAuth.CreatePkcePair();
        var push = await PushAsync(
            client,
            AuthAppFixture.ClientId,
            challenge,
            "openid api.read",
            cancellationToken
        );
        var requestUri = push.GetProperty("request_uri").GetString()!;

        await AuthorizeByRequestUriAsync(
            client,
            AuthAppFixture.ClientId,
            requestUri,
            cancellationToken
        );

        // Replaying the handle must fail: a request_uri that stayed live would turn an
        // intercepted redirect into a repeatable authorization.
        var replay = await client.GetAsync(
            AuthorizeByRequestUriUrl(AuthAppFixture.ClientId, requestUri),
            cancellationToken
        );
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ParRequiredClient_PlainAuthorizeUrl_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, _) = await SignedInClientAsync(cancellationToken);

        var (_, challenge) = OAuth.CreatePkcePair();
        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid api.read", AuthAppFixture.ParClientId),
            cancellationToken
        );

        // The opt-in's teeth: full, valid front-channel parameters, refused anyway.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.ShouldContain("invalid_request");
    }

    [Fact]
    public async Task ParRequiredClient_ThroughPar_CompletesTheCodeFlow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, user) = await SignedInClientAsync(cancellationToken);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var push = await PushAsync(
            client,
            AuthAppFixture.ParClientId,
            challenge,
            "openid email api.read",
            cancellationToken
        );
        var requestUri = push.GetProperty("request_uri").GetString()!;

        var code = await AuthorizeByRequestUriAsync(
            client,
            AuthAppFixture.ParClientId,
            requestUri,
            cancellationToken
        );

        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ParClientId,
                ["code_verifier"] = verifier,
            },
            cancellationToken: cancellationToken
        );

        OAuth
            .DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!)
            .GetProperty("sub")
            .GetString()
            .ShouldBe(user);
    }

    private async Task<(HttpClient Client, string UserId)> SignedInClientAsync(
        CancellationToken cancellationToken
    )
    {
        var email = $"par-{Guid.NewGuid():N}@mystack.test";
        var user = await app.CreateUserAsync(email);

        var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        return (client, user.Id.ToString());
    }

    private static async Task<JsonElement> PushAsync(
        HttpClient client,
        string clientId,
        string challenge,
        string scope,
        CancellationToken cancellationToken
    )
    {
        var response = await client.PostAsync(
            "/connect/par",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["redirect_uri"] = AuthAppFixture.RedirectUri,
                    ["response_type"] = "code",
                    ["scope"] = scope,
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256",
                    ["state"] = "par-state",
                }
            ),
            cancellationToken
        );

        // 201: RFC 9126 §2.2's status for a stored request.
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string AuthorizeByRequestUriUrl(string clientId, string requestUri) =>
        $"/connect/authorize?client_id={clientId}"
        + $"&request_uri={Uri.EscapeDataString(requestUri)}";

    private static async Task<string> AuthorizeByRequestUriAsync(
        HttpClient client,
        string clientId,
        string requestUri,
        CancellationToken cancellationToken
    )
    {
        var response = await client.GetAsync(
            AuthorizeByRequestUriUrl(clientId, requestUri),
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(AuthAppFixture.RedirectUri);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["error"].ShouldBeNull(query["error_description"]);
        query["state"].ShouldBe("par-state");

        return query["code"].ShouldNotBeNull();
    }
}
