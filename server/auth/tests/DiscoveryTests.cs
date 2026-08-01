using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class DiscoveryTests(AuthAppFixture app)
{
    [Fact]
    public async Task Discovery_DescribesTheServerAsBuilt()
    {
        var document = await GetDiscoveryDocumentAsync();

        document
            .GetProperty("authorization_endpoint")
            .GetString()
            .ShouldEndWith("/connect/authorize");
        document.GetProperty("token_endpoint").GetString().ShouldEndWith("/connect/token");
        document.GetProperty("userinfo_endpoint").GetString().ShouldEndWith("/connect/userinfo");
        document
            .GetProperty("introspection_endpoint")
            .GetString()
            .ShouldEndWith("/connect/introspection");
        document
            .GetProperty("end_session_endpoint")
            .GetString()
            .ShouldEndWith("/connect/endsession");
        document
            .GetProperty("revocation_endpoint")
            .GetString()
            .ShouldEndWith("/connect/revocation");
        document
            .GetProperty("device_authorization_endpoint")
            .GetString()
            .ShouldEndWith("/connect/device");
        document
            .GetProperty("pushed_authorization_request_endpoint")
            .GetString()
            .ShouldEndWith("/connect/par");

        // Exactly S256: `plain` is challenge == verifier — none of the interception protection
        // PKCE exists for — and OAuth 2.1 disallows it, so advertising it would be a bug.
        document
            .GetProperty("code_challenge_methods_supported")
            .EnumerateArray()
            .Select(method => method.GetString())
            .ShouldBe(["S256"]);

        // `select_account` would promise behavior nothing here has (one cookie session).
        // `consent` is accepted as satisfied — first-party clients, implicit consent (D17) —
        // because OIDC §11 clients send prompt=consent whenever they ask for offline_access.
        document
            .GetProperty("prompt_values_supported")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ShouldBe(["consent", "login", "none"], ignoreOrder: true);

        var scopes = document
            .GetProperty("scopes_supported")
            .EnumerateArray()
            .Select(scope => scope.GetString())
            .ToList();
        scopes.ShouldContain("api.read");
        scopes.ShouldContain("api.write");
        scopes.ShouldContain("offline_access");

        // The claims the tokens actually carry — the default advertises only the bare
        // protocol five, underselling what a client can ask for.
        var claims = document
            .GetProperty("claims_supported")
            .EnumerateArray()
            .Select(claim => claim.GetString())
            .ToList();
        claims.ShouldBeSubsetOf([
            "aud",
            "exp",
            "iat",
            "iss",
            "sub",
            "email",
            "email_verified",
            "auth_time",
            "role",
        ]);
        claims.ShouldContain("email");
        claims.ShouldContain("email_verified");
        claims.ShouldContain("auth_time");
        claims.ShouldContain("role");

        // Explicitly empty, not absent: an absent key invites the spec's SHOULD defaults
        // (`none` + RS256), and clients would probe request objects this server rejects.
        // PAR is the by-reference channel.
        document
            .GetProperty("request_object_signing_alg_values_supported")
            .GetArrayLength()
            .ShouldBe(0);
        document.GetProperty("request_parameter_supported").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task GrantTypes_AreExactlyTheFourFlows_NeverPassword()
    {
        var document = await GetDiscoveryDocumentAsync();

        var grantTypes = document
            .GetProperty("grant_types_supported")
            .EnumerateArray()
            .Select(grant => grant.GetString())
            .ToList();

        // The exact list is the assertion; password called out anyway because it is the one
        // non-negotiable a copy-paste would reintroduce (architecture §3.4).
        grantTypes.ShouldBe(
            [
                "authorization_code",
                "refresh_token",
                "client_credentials",
                "urn:ietf:params:oauth:grant-type:device_code",
            ],
            ignoreOrder: true
        );
        grantTypes.ShouldNotContain("password");
    }

    [Fact]
    public async Task Jwks_ServesSigningKeys()
    {
        var document = await GetDiscoveryDocumentAsync();
        var jwksUri = new Uri(document.GetProperty("jwks_uri").GetString()!);

        var response = await app.Client.GetAsync(
            jwksUri.PathAndQuery,
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        var jwks = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        jwks.RootElement.GetProperty("keys").GetArrayLength().ShouldBeGreaterThan(0);
    }

    private async Task<JsonElement> GetDiscoveryDocumentAsync()
    {
        var response = await app.Client.GetAsync(
            "/.well-known/openid-configuration",
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
