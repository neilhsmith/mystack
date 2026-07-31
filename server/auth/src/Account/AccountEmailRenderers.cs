using System.Text.Encodings.Web;
using MyStack.Email;

namespace MyStack.Auth.Account;

// The interpolated-string bodies behind MyStack.Email's renderer seam. Both bodies always, so the
// multipart/alternative posture holds; the full URL is printed as well as linked because "copy the
// link into another browser" is a flow people actually use (auth-track step 8).

public sealed record ConfirmationEmail(string Link);

internal sealed class ConfirmationEmailRenderer : IEmailRenderer<ConfirmationEmail>
{
    public EmailContent Render(ConfirmationEmail model)
    {
        var link = HtmlEncoder.Default.Encode(model.Link);

        return new EmailContent(
            "Confirm your MyStack email",
            $"""
            <p>Welcome to MyStack. Confirm your email address to activate your account:</p>
            <p><a href="{link}">Confirm my email</a></p>
            <p>If the link doesn't work, open this URL:<br>{link}</p>
            """,
            $"Welcome to MyStack. Confirm your email: {model.Link}"
        );
    }
}

public sealed record PasswordResetEmail(string Link);

internal sealed class PasswordResetEmailRenderer : IEmailRenderer<PasswordResetEmail>
{
    public EmailContent Render(PasswordResetEmail model)
    {
        var link = HtmlEncoder.Default.Encode(model.Link);

        return new EmailContent(
            "Reset your MyStack password",
            $"""
            <p>We received a request to reset your MyStack password.</p>
            <p><a href="{link}">Reset my password</a></p>
            <p>If the link doesn't work, open this URL:<br>{link}</p>
            <p>If you didn't request this, you can safely ignore this email.</p>
            """,
            $"Reset your MyStack password: {model.Link}\n\n"
                + "If you didn't request this, you can safely ignore this email."
        );
    }
}

public sealed record PasswordChangedEmail(string ForgotPasswordLink);

internal sealed class PasswordChangedEmailRenderer : IEmailRenderer<PasswordChangedEmail>
{
    public EmailContent Render(PasswordChangedEmail model)
    {
        var link = HtmlEncoder.Default.Encode(model.ForgotPasswordLink);

        return new EmailContent(
            "Your MyStack password was changed",
            $"""
            <p>Your MyStack password was just changed.</p>
            <p>If this was you, no further action is needed.</p>
            <p>If it wasn't, someone else knows your password. Reset it now:<br>
            <a href="{link}">{link}</a></p>
            """,
            "Your MyStack password was just changed.\n\n"
                + "If this was you, no further action is needed. If it wasn't, someone else "
                + $"knows your password. Reset it now: {model.ForgotPasswordLink}"
        );
    }
}
