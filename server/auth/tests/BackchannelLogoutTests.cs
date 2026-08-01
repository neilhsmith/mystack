using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class BackchannelLogoutTests(AuthAppFixture app)
{
    private const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    [Fact]
    public async Task EndSession_DeliversAValidLogoutTokenToEveryRegisteredClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        app.RelyingParty.Clear();
        app.SecondRelyingParty.Clear();

        var email = $"backchannel-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        using var notifications = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.logout_notifications"
        );

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await OAuth.EndSessionAsync(
            client,
            $"?client_id={AuthAppFixture.ClientId}"
                + $"&post_logout_redirect_uri={Uri.EscapeDataString(AuthAppFixture.PostLogoutRedirectUri)}",
            cancellationToken
        );
        response.StatusCode.ShouldBe(HttpStatusCode.Found);

        // Delivery completes before the sign-out response returns, so both fakes hold their
        // POST already — no polling. Sign-out propagates to every registered client with a
        // logout URI, not just the one that initiated it.
        var first = app.RelyingParty.Received.ShouldHaveSingleItem();
        var second = app.SecondRelyingParty.Received.ShouldHaveSingleItem();
        first.ContentType.ShouldNotBeNull();
        first.ContentType.ShouldStartWith("application/x-www-form-urlencoded");

        await AssertValidLogoutTokenAsync(
            first.LogoutToken,
            AuthAppFixture.ClientId,
            user.Id.ToString(),
            cancellationToken
        );
        await AssertValidLogoutTokenAsync(
            second.LogoutToken,
            AuthAppFixture.ParClientId,
            user.Id.ToString(),
            cancellationToken
        );

        // The unreachable client was attempted and failed without blocking the response or the
        // deliveries above.
        var outcomes = notifications
            .GetMeasurementSnapshot()
            .Select(measurement =>
                (
                    ClientId: (string?)measurement.Tags["client_id"],
                    Outcome: (string?)measurement.Tags["outcome"]
                )
            )
            .ToList();
        outcomes.ShouldContain((AuthAppFixture.ClientId, "delivered"));
        outcomes.ShouldContain((AuthAppFixture.ParClientId, "delivered"));
        outcomes.ShouldContain((AuthAppFixture.UnreachableClientId, "failed"));
    }

    [Fact]
    public async Task EndSession_WithNoSessionAndNoHint_NotifiesNobody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        app.RelyingParty.Clear();
        app.SecondRelyingParty.Clear();

        using var client = app.CreateFlowClient();
        var response = await OAuth.EndSessionAsync(client, cancellationToken: cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        app.RelyingParty.Received.ShouldBeEmpty();
        app.SecondRelyingParty.Received.ShouldBeEmpty();
    }

    // The BFF-initiated shape: the client's own session already ended, the auth cookie may have
    // expired too, and the redirect carries id_token_hint — the other apps still hear about it.
    [Fact]
    public async Task EndSession_WithAnIdTokenHint_NotifiesWhenTheCookieIsAlreadyGone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        app.RelyingParty.Clear();

        var email = $"backchannel-hint-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        using var signedIn = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            signedIn,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            signedIn,
            challenge,
            "openid email",
            cancellationToken
        );
        var tokens = await OAuth.ExchangeAsync(
            signedIn,
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
        var idToken = tokens.GetProperty("id_token").GetString()!;

        using var anonymous = app.CreateFlowClient();
        var response = await anonymous.GetAsync(
            $"/connect/endsession?id_token_hint={idToken}&client_id={AuthAppFixture.ClientId}",
            cancellationToken
        );
        response.StatusCode.ShouldBe(HttpStatusCode.Found);

        var received = app.RelyingParty.Received.ShouldHaveSingleItem();
        OAuth
            .DecodeJwtPayload(received.LogoutToken)
            .GetProperty("sub")
            .GetString()
            .ShouldBe(user.Id.ToString());
    }

    // "Valid" the way a consumer decides it: signature against the published JWKS, issuer
    // against the discovery document, audience, lifetime, and the `logout+jwt` type header —
    // then the spec's claim shape, including the nonce prohibition that stops a logout token
    // doubling as an id token.
    private async Task AssertValidLogoutTokenAsync(
        string token,
        string audience,
        string subject,
        CancellationToken cancellationToken
    )
    {
        var discovery = JsonDocument
            .Parse(
                await app.Client.GetStringAsync(
                    "/.well-known/openid-configuration",
                    cancellationToken
                )
            )
            .RootElement;
        var jwks = new JsonWebKeySet(
            await app.Client.GetStringAsync(
                discovery.GetProperty("jwks_uri").GetString()!,
                cancellationToken
            )
        );

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = discovery.GetProperty("issuer").GetString(),
                ValidAudience = audience,
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidTypes = ["logout+jwt"],
            }
        );
        result.IsValid.ShouldBeTrue(result.Exception?.ToString());

        var payload = OAuth.DecodeJwtPayload(token);
        payload.GetProperty("sub").GetString().ShouldBe(subject);
        payload.GetProperty("jti").GetString().ShouldNotBeNullOrEmpty();
        payload.GetProperty("iat").ValueKind.ShouldBe(JsonValueKind.Number);
        payload.GetProperty("exp").ValueKind.ShouldBe(JsonValueKind.Number);
        payload.GetProperty("events").TryGetProperty(LogoutEvent, out _).ShouldBeTrue();
        payload.TryGetProperty("nonce", out _).ShouldBeFalse();
    }
}
