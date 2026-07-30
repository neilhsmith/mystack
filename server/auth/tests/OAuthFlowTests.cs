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

    private static string? Audience(JsonElement claims)
    {
        var audience = claims.GetProperty("aud");
        return audience.ValueKind == JsonValueKind.Array
            ? audience[0].GetString()
            : audience.GetString();
    }
}
