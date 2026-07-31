using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyStack.Auth.Data;
using MyStack.Email;

namespace MyStack.Auth.Account;

// Composes the account emails as SendEmail commands for the worker's queue. Publishing is the
// caller's job: the page handler owns the transaction the message must ride in.
// Public because the public page models take it by constructor (CS0051).
public sealed class AccountEmails(
    UserManager<ApplicationUser> users,
    IOptions<AccountOptions> options,
    IEmailRenderer<ConfirmationEmail> confirmation,
    IEmailRenderer<PasswordResetEmail> passwordReset,
    IEmailRenderer<PasswordChangedEmail> passwordChanged
)
{
    public async Task<SendEmail> ComposeConfirmationAsync(ApplicationUser user)
    {
        var token = AccountTokens.Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = Link("/confirm-email", user, token);

        return new SendEmail(confirmation.Render(new ConfirmationEmail(link)).ToMessage(To(user)));
    }

    public async Task<SendEmail> ComposePasswordResetAsync(ApplicationUser user)
    {
        var token = AccountTokens.Encode(await users.GeneratePasswordResetTokenAsync(user));
        var link = Link("/reset-password", user, token);

        return new SendEmail(
            passwordReset.Render(new PasswordResetEmail(link)).ToMessage(To(user))
        );
    }

    public SendEmail ComposePasswordChanged(ApplicationUser user)
    {
        var link = BaseUrl() + "/forgot-password";

        return new SendEmail(
            passwordChanged.Render(new PasswordChangedEmail(link)).ToMessage(To(user))
        );
    }

    private string Link(string path, ApplicationUser user, string token) =>
        BaseUrl()
        + path
        + QueryString
            .Create([
                new KeyValuePair<string, string?>("userId", user.Id.ToString()),
                new KeyValuePair<string, string?>("token", token),
            ])
            .ToUriComponent();

    private string BaseUrl() => options.Value.PublicBaseUrl.TrimEnd('/');

    private static EmailAddress To(ApplicationUser user) => new(user.Email!);
}
