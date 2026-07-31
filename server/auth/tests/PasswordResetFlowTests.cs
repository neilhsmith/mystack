using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class PasswordResetFlowTests(AuthAppFixture app)
{
    private const string GenericPanel = "If that address has an account, we've sent a password";
    private const string NewPassword = "an entirely different passphrase";

    [Fact]
    public async Task Forgot_Reset_SignIn_TheWholePath_AndTheTokenIsSingleUse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"reset-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var requested = await PageForms.SubmitAsync(
            client,
            "/forgot-password",
            new Dictionary<string, string> { ["Email"] = email },
            cancellationToken
        );
        (await requested.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(GenericPanel);

        var sent = await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken);
        sent.Email.Subject.ShouldBe("Reset your MyStack password");
        var (linkPath, _, _) = sent.ParseAccountLink();
        linkPath.ShouldStartWith("/reset-password?");

        var done = await PageForms.SubmitAsync(
            client,
            linkPath,
            ResetForm(NewPassword),
            cancellationToken
        );
        (await done.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("Password reset");

        // The owner hears about the change.
        (
            await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken)
        ).Email.Subject.ShouldBe("Your MyStack password was changed");

        // Old credential dead, new one live.
        (
            await (
                await OAuth.SignInAsync(
                    client,
                    email,
                    AuthAppFixture.DefaultPassword,
                    cancellationToken: cancellationToken
                )
            ).Content.ReadAsStringAsync(cancellationToken)
        ).ShouldContain("The email or password is incorrect.");
        (
            await OAuth.SignInAsync(
                client,
                email,
                NewPassword,
                cancellationToken: cancellationToken
            )
        ).StatusCode.ShouldBe(HttpStatusCode.Found);

        // ResetPasswordAsync rotated the security stamp, so the emailed token died with its use.
        var reused = await PageForms.SubmitAsync(
            client,
            linkPath,
            ResetForm("yet another perfectly fine passphrase"),
            cancellationToken
        );
        (await reused.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "The reset link is invalid or has expired."
        );
    }

    [Fact]
    public async Task Forgot_UnknownAndUnconfirmed_LookIdenticalAndSendNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var resets = CreateCollector("auth.password_resets");
        var unknown = $"forgot-none-{Guid.NewGuid():N}@example.test";
        var unconfirmed = $"forgot-unconfirmed-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(unconfirmed, emailConfirmed: false);

        using var client = app.CreateFlowClient();
        foreach (var email in new[] { unknown, unconfirmed })
        {
            var response = await PageForms.SubmitAsync(
                client,
                "/forgot-password",
                new Dictionary<string, string> { ["Email"] = email },
                cancellationToken
            );
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
                GenericPanel
            );
        }

        await WorkerQueue.EnsureNoEmailAsync(app, unknown, cancellationToken);
        await WorkerQueue.EnsureNoEmailAsync(app, unconfirmed, cancellationToken);

        var outcomes = resets
            .GetMeasurementSnapshot()
            .Where(measurement => (string?)measurement.Tags["stage"] == "requested")
            .Select(measurement => (string?)measurement.Tags["outcome"])
            .ToList();
        outcomes.ShouldContain("unknown_email");
        outcomes.ShouldContain("unconfirmed_email");
    }

    [Fact]
    public async Task Reset_WeakPassword_ShowsThePolicy_AndTheTokenSurvivesToRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"reset-weak-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        await PageForms.SubmitAsync(
            client,
            "/forgot-password",
            new Dictionary<string, string> { ["Email"] = email },
            cancellationToken
        );
        var (linkPath, _, _) = (
            await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken)
        ).ParseAccountLink();

        // Policy errors only surface after a valid token, so they probe nothing — and a failed
        // attempt must not burn the token.
        var weak = await PageForms.SubmitAsync(
            client,
            linkPath,
            ResetForm("short"),
            cancellationToken
        );
        var weakHtml = await weak.Content.ReadAsStringAsync(cancellationToken);
        weakHtml.ShouldContain("at least 12 characters");
        weakHtml.ShouldNotContain("Reset failed");

        var retried = await PageForms.SubmitAsync(
            client,
            linkPath,
            ResetForm(NewPassword),
            cancellationToken
        );
        (await retried.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Password reset"
        );
    }

    [Fact]
    public async Task Reset_TamperedLinks_CollapseToOneGenericFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"reset-bad-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        foreach (
            var url in new[]
            {
                $"/reset-password?userId={user.Id}&token=not!base64url",
                $"/reset-password?userId={user.Id}&token=bm90LWEtcmVhbC10b2tlbg",
                $"/reset-password?userId={Guid.NewGuid()}&token=bm90LWEtcmVhbC10b2tlbg",
            }
        )
        {
            var response = await PageForms.SubmitAsync(
                client,
                url,
                ResetForm(NewPassword),
                cancellationToken
            );
            (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
                "The reset link is invalid or has expired."
            );
        }
    }

    [Fact]
    public async Task Forgot_ActivatesAPasswordlessAccount()
    {
        // Production seeding declares addresses without passwords; activation is this exact
        // path — forgot, emailed link, choose the first password (architecture §3.4).
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"activate-{Guid.NewGuid():N}@example.test";

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(
                new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                }
            );
            created.Succeeded.ShouldBeTrue();
        }

        using var client = app.CreateFlowClient();
        await PageForms.SubmitAsync(
            client,
            "/forgot-password",
            new Dictionary<string, string> { ["Email"] = email },
            cancellationToken
        );
        var (linkPath, _, _) = (
            await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken)
        ).ParseAccountLink();

        var done = await PageForms.SubmitAsync(
            client,
            linkPath,
            ResetForm(NewPassword),
            cancellationToken
        );
        (await done.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("Password reset");

        (
            await OAuth.SignInAsync(
                client,
                email,
                NewPassword,
                cancellationToken: cancellationToken
            )
        ).StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    private static Dictionary<string, string> ResetForm(string password) =>
        new() { ["Password"] = password, ["ConfirmPassword"] = password };

    private MetricCollector<long> CreateCollector(string instrument) =>
        new(app.Services.GetRequiredService<IMeterFactory>(), AuthMetrics.MeterName, instrument);
}
