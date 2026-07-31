using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class ClientCredentialsTests(AuthAppFixture app)
{
    [Fact]
    public async Task MachineClient_GetsItsOwnToken_CarryingNoUser()
    {
        using var grants = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.oauth.grants"
        );

        var body = await OAuth.ExchangeAsync(
            app.Client,
            MachineTokenRequest(scope: "api.read"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Nothing user-shaped may appear: no refresh token (no offline_access, no session to
        // outlive) and no id token (nobody to identify).
        body.TryGetProperty("refresh_token", out _).ShouldBeFalse();
        body.TryGetProperty("id_token", out _).ShouldBeFalse();

        var claims = OAuth.DecodeJwtPayload(body.GetProperty("access_token").GetString()!);
        claims.GetProperty("sub").GetString().ShouldBe(AuthAppFixture.MachineClientId);
        claims.GetProperty("scope").GetString().ShouldBe("api.read");
        claims.GetProperty("aud").GetString().ShouldBe("api");
        claims.TryGetProperty("email", out _).ShouldBeFalse();
        claims.TryGetProperty("role", out _).ShouldBeFalse();
        claims.TryGetProperty("perm", out _).ShouldBeFalse();
        claims.TryGetProperty("perm_deny", out _).ShouldBeFalse();

        grants
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["grant_type"] == "client_credentials"
                && (string?)measurement.Tags["result"] == "issued"
            );
    }

    [Fact]
    public async Task WrongSecret_IsRejected()
    {
        var body = await OAuth.ExchangeAsync(
            app.Client,
            MachineTokenRequest(scope: "api.read", secret: "not the machine's secret"),
            HttpStatusCode.Unauthorized,
            TestContext.Current.CancellationToken
        );

        body.GetProperty("error").GetString().ShouldBe("invalid_client");
    }

    [Fact]
    public async Task PublicClient_CannotUseClientCredentials()
    {
        var body = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            TestContext.Current.CancellationToken
        );

        // A public client has no secret, so the grant is structurally unusable — OpenIddict
        // refuses at the missing-credential check before client type is even considered.
        body.GetProperty("error").GetString().ShouldBe("invalid_request");
        body.GetProperty("error_description").GetString()!.ShouldContain("client_secret");
    }

    [Fact]
    public async Task UngrantedScope_IsRejected()
    {
        var body = await OAuth.ExchangeAsync(
            app.Client,
            MachineTokenRequest(scope: "api.read api.write"),
            HttpStatusCode.BadRequest,
            TestContext.Current.CancellationToken
        );

        // OpenIddict reports a scope-permission failure as invalid_request; the description
        // carries the actual reason.
        body.GetProperty("error").GetString().ShouldBe("invalid_request");
        body.GetProperty("error_description").GetString()!.ShouldContain("scope");
    }

    [Fact]
    public async Task MachineToken_HasNoUserinfoAnswer()
    {
        var body = await OAuth.ExchangeAsync(
            app.Client,
            MachineTokenRequest(scope: "api.read"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.GetProperty("access_token").GetString()
        );

        var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        // The token is valid, but there is no user behind it to describe.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_token");
    }

    private static Dictionary<string, string> MachineTokenRequest(
        string scope,
        string secret = AuthAppFixture.MachineClientSecret
    ) =>
        new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthAppFixture.MachineClientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        };
}
