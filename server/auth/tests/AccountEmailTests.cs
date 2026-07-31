using MyStack.Auth.Account;
using Shouldly;

namespace MyStack.Auth.Tests;

// The renderers are pure — subject and both bodies, provable without a host.
public sealed class AccountEmailTests
{
    private const string Link = "http://localhost:5100/confirm-email?userId=abc&token=xyz";

    [Fact]
    public void Confirmation_CarriesTheLinkInBothBodies()
    {
        var content = new ConfirmationEmailRenderer().Render(new ConfirmationEmail(Link));

        content.Subject.ShouldBe("Confirm your MyStack email");
        content.HtmlBody.ShouldContain("confirm-email?userId=abc");
        content.TextBody.ShouldContain(Link);
    }

    [Fact]
    public void PasswordReset_CarriesTheLinkInBothBodies()
    {
        const string resetLink = "http://localhost:5100/reset-password?userId=abc&token=xyz";
        var content = new PasswordResetEmailRenderer().Render(new PasswordResetEmail(resetLink));

        content.Subject.ShouldBe("Reset your MyStack password");
        content.HtmlBody.ShouldContain("reset-password?userId=abc");
        content.TextBody.ShouldContain(resetLink);
        content.TextBody.ShouldContain("you can safely ignore this email");
    }

    [Fact]
    public void PasswordChanged_PointsAtTheForgotPasswordPage()
    {
        var content = new PasswordChangedEmailRenderer().Render(
            new PasswordChangedEmail("http://localhost:5100/forgot-password")
        );

        content.Subject.ShouldBe("Your MyStack password was changed");
        content.HtmlBody.ShouldContain("/forgot-password");
        content.TextBody.ShouldContain("/forgot-password");
    }

    [Fact]
    public void TokenTransform_RoundTrips_AndRefusesGarbageQuietly()
    {
        // Identity's tokens are standard Base64 — exactly the characters query strings mangle.
        const string token = "CfDJ8Nx+abc/def==";

        var encoded = AccountTokens.Encode(token);
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("=");

        AccountTokens.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(token);

        // A tampered link is an invalid token, never a FormatException.
        AccountTokens.TryDecode("not!base64url", out _).ShouldBeFalse();
        AccountTokens.TryDecode(null, out _).ShouldBeFalse();
        AccountTokens.TryDecode("", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AWrongPublicBaseUrl_FailsTheBoot()
    {
        // On Unix a rooted path parses as an absolute file: URI, so the scheme check is what
        // actually catches "/auth" here.
        await using var factory = new AuthApplicationFactory(
            "Host=localhost;Database=never-reached",
            "amqp://never-reached",
            settings: new Dictionary<string, string?> { ["Account:PublicBaseUrl"] = "/auth" }
        );

        Should
            .Throw<InvalidOperationException>(() => factory.CreateClient())
            .Message.ShouldContain("Account:PublicBaseUrl");
    }
}
