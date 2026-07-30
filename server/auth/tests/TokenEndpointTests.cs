using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class TokenEndpointTests(AuthAppFixture app)
{
    [Fact]
    public async Task PasswordGrant_IsUnsupported_AndCounted()
    {
        using var grants = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.oauth.grants"
        );

        var body = await OAuth.ExchangeAsync(
            app.Client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = "someone@example.test",
                ["password"] = "whatever else is true",
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            TestContext.Current.CancellationToken
        );

        body.GetProperty("error").GetString().ShouldBe("unsupported_grant_type");

        // The counter collapses client-supplied grant types to a closed set, and it records the
        // rejection even though the request never reached the endpoint handler.
        grants
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["grant_type"] == "unsupported"
                && (string?)measurement.Tags["result"] == "unsupported_grant_type"
            );
    }

    [Fact]
    public async Task StolenCode_WithoutItsVerifier_IsRejected()
    {
        var email = $"pkce-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: TestContext.Current.CancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (_, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            client,
            challenge,
            "openid",
            TestContext.Current.CancellationToken
        );

        var (wrongVerifier, _) = OAuth.CreatePkcePair();
        var body = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ClientId,
                ["code_verifier"] = wrongVerifier,
            },
            HttpStatusCode.BadRequest,
            TestContext.Current.CancellationToken
        );

        body.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }
}
