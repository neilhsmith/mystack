using System.Net;
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

        var response = await PageForms.SubmitAsync(
            client,
            "/change-password",
            ChangeForm(AuthAppFixture.DefaultPassword, NewPassword),
            cancellationToken
        );
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Password changed"
        );

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

    private static Dictionary<string, string> ChangeForm(string current, string next) =>
        new()
        {
            ["CurrentPassword"] = current,
            ["NewPassword"] = next,
            ["ConfirmPassword"] = next,
        };
}
