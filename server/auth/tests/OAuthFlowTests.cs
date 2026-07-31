using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class OAuthFlowTests(AuthAppFixture app)
{
    // The step-4 proof: a manually registered client completes the whole flow — sign in,
    // authorize with PKCE, exchange, refresh with rotation, revoke.
    [Fact]
    public async Task CodeFlowWithPkce_IssuesRefreshesAndRevokes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"flow-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email, role: "auditor");

        using var client = app.CreateFlowClient();
        using var grants = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.oauth.grants"
        );

        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        const string scope = "openid email profile roles offline_access api.read";
        var code = await OAuth.AuthorizeAsync(client, challenge, scope, cancellationToken);

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

        // Oidc:AccessTokenLifetime — appsettings' fifteen minutes reaching the wire.
        tokens.GetProperty("expires_in").GetInt32().ShouldBeInRange(840, 900);

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var claims = OAuth.DecodeJwtPayload(accessToken);
        claims.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        claims.GetProperty("email").GetString().ShouldBe(email);
        claims.GetProperty("role").GetString().ShouldBe("auditor");
        Audience(claims).ShouldBe("api");
        // Identity's principal carries more than a token may: deny-by-default destinations keep
        // the security stamp — and anything else unlisted — out.
        claims.TryGetProperty("AspNet.Identity.SecurityStamp", out _).ShouldBeFalse();

        var identityClaims = OAuth.DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!);
        identityClaims.GetProperty("email").GetString().ShouldBe(email);
        identityClaims.GetProperty("role").GetString().ShouldBe("auditor");

        grants
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["grant_type"] == "authorization_code"
                && (string?)measurement.Tags["result"] == "issued"
            );

        var refreshed = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            cancellationToken: cancellationToken
        );

        // Rotation: a refresh yields a new refresh token rather than extending the old one.
        refreshed.GetProperty("refresh_token").GetString().ShouldNotBe(refreshToken);
        var rotated = refreshed.GetProperty("refresh_token").GetString()!;

        grants
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["grant_type"] == "refresh_token"
                && (string?)measurement.Tags["result"] == "issued"
            );

        var revoke = await client.PostAsync(
            "/connect/revocation",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["token"] = rotated,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = AuthAppFixture.ClientId,
                }
            ),
            cancellationToken
        );
        revoke.EnsureSuccessStatusCode();

        var rejected = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = rotated,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        rejected.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    // Scope gates the access token too: granted only api.read, the token authorizes API calls
    // and identifies nobody — email, name and role stay out of an unencrypted JWT the client
    // never asked to carry them.
    [Fact]
    public async Task AccessToken_WithoutIdentityScopes_CarriesNoIdentityClaims()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"narrow-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email, role: "auditor");

        var tokens = await TokensAsync("openid api.read", email, cancellationToken);

        var claims = OAuth.DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!);
        claims.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        Audience(claims).ShouldBe("api");
        claims.TryGetProperty("email", out _).ShouldBeFalse();
        claims.TryGetProperty("name", out _).ShouldBeFalse();
        claims.TryGetProperty("role", out _).ShouldBeFalse();
    }

    // OIDC requires auth_time in the id token whenever the client sent max_age — and a refresh
    // must carry the original authentication time forward, because refreshing is not
    // authenticating.
    [Fact]
    public async Task IdToken_CarriesAuthTime_AndARefreshPreservesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"authtime-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        var tokens = await TokensAsync("openid offline_access", email, cancellationToken);

        var authTime = OAuth
            .DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!)
            .GetProperty("auth_time")
            .GetInt64();
        authTime.ShouldBeInRange(
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );

        var refreshed = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            cancellationToken: cancellationToken
        );

        OAuth
            .DecodeJwtPayload(refreshed.GetProperty("id_token").GetString()!)
            .GetProperty("auth_time")
            .GetInt64()
            .ShouldBe(authTime);
    }

    // The stolen-token detection story: replaying a refresh token that was already rotated away
    // revokes the whole grant chain, so the thief's copy and the legitimate one both die.
    // (Testing zeroes OpenIddict's 30-second reuse leeway so the replay is observable.)
    [Fact]
    public async Task ReplayingARotatedRefreshToken_RevokesTheWholeChain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"replay-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        var tokens = await TokensAsync("openid offline_access", email, cancellationToken);
        var original = tokens.GetProperty("refresh_token").GetString()!;

        var refreshed = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = original,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            cancellationToken: cancellationToken
        );
        var rotated = refreshed.GetProperty("refresh_token").GetString()!;

        var replayed = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = original,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        replayed.GetProperty("error").GetString().ShouldBe("invalid_grant");

        // The legitimate holder's rotated token died with the chain — the point of the design:
        // theft turns into a forced re-authentication instead of a silent parallel session.
        var chainDead = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = rotated,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        chainDead.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    private async Task<JsonElement> TokensAsync(
        string scope,
        string email,
        CancellationToken cancellationToken
    )
    {
        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(client, challenge, scope, cancellationToken);

        return await OAuth.ExchangeAsync(
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
    }

    private static string? Audience(JsonElement claims)
    {
        var audience = claims.GetProperty("aud");
        return audience.ValueKind == JsonValueKind.Array
            ? audience[0].GetString()
            : audience.GetString();
    }
}
