using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class SignInPageTests(AuthAppFixture app)
{
    // One answer for every failure: anything more specific confirms the account exists.
    private const string GenericError = "The email or password is incorrect.";

    [Fact]
    public async Task Get_RendersTheForm_UnderThePagesHeaderPolicy()
    {
        using var client = app.CreateFlowClient();

        var response = await client.GetAsync("/signin", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("__RequestVerificationToken");

        // The pages policy loosens exactly one directive; everywhere else form-action stays shut.
        Csp(response).ShouldContain("form-action 'self'");
        var discovery = await client.GetAsync(
            "/.well-known/openid-configuration",
            TestContext.Current.CancellationToken
        );
        Csp(discovery).ShouldContain("form-action 'none'");
    }

    [Fact]
    public async Task UnknownEmail_AndWrongPassword_GetTheSameAnswer()
    {
        using var signIns = CreateSignInCollector();
        var email = $"signin-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();

        var unknown = await OAuth.SignInAsync(
            client,
            $"unknown-{Guid.NewGuid():N}@example.test",
            AuthAppFixture.DefaultPassword,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var wrongPassword = await OAuth.SignInAsync(
            client,
            email,
            "not the right passphrase",
            cancellationToken: TestContext.Current.CancellationToken
        );

        foreach (var response in new[] { unknown, wrongPassword })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken
            );
            html.ShouldContain(GenericError);
        }

        signIns
            .GetMeasurementSnapshot()
            .Count(measurement => (string?)measurement.Tags["result"] == "invalid_credentials")
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task UnconfirmedEmail_IsIndistinguishable_ButHonestInTheMetric()
    {
        using var signIns = CreateSignInCollector();
        var email = $"unconfirmed-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, emailConfirmed: false);

        using var client = app.CreateFlowClient();
        var response = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain(GenericError);

        // The response hides the reason; the operator's view of it lives here.
        signIns
            .GetMeasurementSnapshot()
            .ShouldContain(measurement => (string?)measurement.Tags["result"] == "not_allowed");
    }

    [Fact]
    public async Task ValidCredentials_SignInAndFollowTheLocalReturnUrl()
    {
        using var signIns = CreateSignInCollector();
        var email = $"valid-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var response = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            returnUrl: "/somewhere-local",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.ToString().ShouldBe("/somewhere-local");
        response
            .Headers.GetValues("Set-Cookie")
            .ShouldContain(cookie => cookie.StartsWith(".AspNetCore.Identity.Application"));

        signIns
            .GetMeasurementSnapshot()
            .ShouldContain(measurement => (string?)measurement.Tags["result"] == "success");
    }

    [Fact]
    public async Task ExternalReturnUrl_FallsBackToTheRoot()
    {
        var email = $"redirect-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var response = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            returnUrl: "https://evil.example/phish",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.ToString().ShouldBe("/");
    }

    // Five wrong guesses lock the account; even the right password then gets the same generic
    // answer, so lockout is invisible from the outside and honest only in the metric.
    [Fact]
    public async Task RepeatedFailures_LockTheAccount_BehindTheSameGenericAnswer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var signIns = CreateSignInCollector();
        var email = $"lockout-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failure = await OAuth.SignInAsync(
                client,
                email,
                "not the right passphrase",
                cancellationToken: cancellationToken
            );
            failure.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var lockedOut = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );

        // The correct password no longer signs in — and the page says exactly what it always
        // says.
        lockedOut.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await lockedOut.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(GenericError);
        signIns
            .GetMeasurementSnapshot()
            .ShouldContain(measurement => (string?)measurement.Tags["result"] == "locked_out");
    }

    // Every credential form rides antiforgery; a POST without the token must die before the
    // handler, not reach it.
    [Fact]
    public async Task Post_WithoutTheAntiforgeryToken_IsRejected()
    {
        var email = $"forgery-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var response = await client.PostAsync(
            "/signin",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = email,
                    ["Password"] = AuthAppFixture.DefaultPassword,
                }
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            cookies.ShouldNotContain(
                cookie => cookie.StartsWith(".AspNetCore.Identity.Application"),
                "nothing should have signed in"
            );
        }
    }

    private MetricCollector<long> CreateSignInCollector() =>
        new(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.sign_ins"
        );

    private static string Csp(HttpResponseMessage response) =>
        string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
}
