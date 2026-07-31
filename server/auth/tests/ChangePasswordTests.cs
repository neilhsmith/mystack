using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class ChangePasswordTests(AuthAppFixture app)
{
    private const string NewPassword = "a freshly rotated passphrase";

    [Fact]
    public async Task Anonymous_IsChallengedToSignIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = app.CreateFlowClient();

        var response = await client.GetAsync("/change-password", cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.PathAndQuery.ShouldStartWith("/signin");
        response.Headers.Location!.PathAndQuery.ShouldContain("ReturnUrl=%2Fchange-password");
    }

    [Fact]
    public async Task WrongCurrentPassword_IsToldSo_AndNothingChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"change-wrong-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );

        using var changes = CreateChangeCollector();
        var response = await PageForms.SubmitAsync(
            client,
            "/change-password",
            ChangeForm("not the right passphrase", NewPassword),
            cancellationToken
        );

        // The caller is already this account's session, so the specific answer reveals nothing.
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Incorrect password"
        );
        changes
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["outcome"] == "wrong_current_password"
            );
        await WorkerQueue.EnsureNoEmailAsync(app, email, cancellationToken);

        using var freshClient = app.CreateFlowClient();
        (
            await OAuth.SignInAsync(
                freshClient,
                email,
                AuthAppFixture.DefaultPassword,
                cancellationToken: cancellationToken
            )
        ).StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task WeakNewPassword_ShowsThePolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"change-weak-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );

        var response = await PageForms.SubmitAsync(
            client,
            "/change-password",
            ChangeForm(AuthAppFixture.DefaultPassword, "short"),
            cancellationToken
        );

        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "at least 12 characters"
        );
    }

    [Fact]
    public async Task Change_RotatesThePassword_NotifiesAndRevokesOtherGrants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"change-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        // First, become "another device": run the code + PKCE flow and hold a refresh token.
        using var client = app.CreateFlowClient();
        await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            client,
            challenge,
            "openid offline_access",
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
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        using var changes = CreateChangeCollector();
        var response = await PageForms.SubmitAsync(
            client,
            "/change-password",
            ChangeForm(AuthAppFixture.DefaultPassword, NewPassword),
            cancellationToken
        );
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Password changed"
        );
        changes
            .GetMeasurementSnapshot()
            .ShouldContain(measurement => (string?)measurement.Tags["outcome"] == "changed");

        // The owner hears about it, the other device's refresh token dies, and the browser that
        // made the change keeps its session.
        (
            await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken)
        ).Email.Subject.ShouldBe("Your MyStack password was changed");

        var refreshed = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        refreshed.GetProperty("error").GetString().ShouldBe("invalid_grant");

        (await client.GetAsync("/change-password", cancellationToken)).StatusCode.ShouldBe(
            HttpStatusCode.OK
        );

        // And the credential really rotated.
        using var freshClient = app.CreateFlowClient();
        (
            await OAuth.SignInAsync(
                freshClient,
                email,
                NewPassword,
                cancellationToken: cancellationToken
            )
        ).StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    // The change-password form verifies the current password, so a hijacked cookie must not get
    // unlimited guesses at it: the sign-in lockout counts here too, and once locked even the
    // correct password is refused.
    [Fact]
    public async Task RepeatedWrongCurrentPasswords_LockTheAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"change-lockout-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );

        using var changes = CreateChangeCollector();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await PageForms.SubmitAsync(
                client,
                "/change-password",
                ChangeForm("not the right passphrase", NewPassword),
                cancellationToken
            );
        }

        var lockedOut = await PageForms.SubmitAsync(
            client,
            "/change-password",
            ChangeForm(AuthAppFixture.DefaultPassword, NewPassword),
            cancellationToken
        );

        (await lockedOut.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Too many incorrect attempts"
        );
        changes
            .GetMeasurementSnapshot()
            .ShouldContain(measurement => (string?)measurement.Tags["outcome"] == "locked_out");
    }

    private MetricCollector<long> CreateChangeCollector() =>
        new(
            app.Services.GetRequiredService<IMeterFactory>(),
            AuthMetrics.MeterName,
            "auth.password_changes"
        );

    private static Dictionary<string, string> ChangeForm(string current, string next) =>
        new()
        {
            ["CurrentPassword"] = current,
            ["NewPassword"] = next,
            ["ConfirmPassword"] = next,
        };
}
