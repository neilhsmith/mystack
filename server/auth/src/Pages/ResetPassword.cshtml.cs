using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Oidc;
using MyStack.Auth.Telemetry;
using Wolverine.EntityFrameworkCore;

namespace MyStack.Auth.Pages;

// Same shape as ConfirmEmail: the emailed link GETs a form that changes nothing, the POST is what
// consumes the token — here it's even more natural, since a reset needs the new password typed in
// anyway (auth-track step 8).
public sealed class ResetPasswordModel(
    UserManager<ApplicationUser> users,
    AccountEmails emails,
    TokenRevocationService revocation,
    IDbContextOutbox<AuthDbContext> outbox,
    AuthMetrics metrics
) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    [Required]
    public string? Password { get; set; }

    [BindProperty]
    [Required]
    [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    public string? ConfirmPassword { get; set; }

    public bool Done { get; private set; }

    public bool Failed { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = Guid.TryParse(UserId, out _) ? await users.FindByIdAsync(UserId!) : null;
        if (user is null || !AccountTokens.TryDecode(Token, out var token))
        {
            metrics.PasswordReset("completed", user is null ? "unknown_user" : "invalid_token");
            Failed = true;
            return Page();
        }

        // One transaction: the new password hash, the revoked grants and the notification email
        // commit together (the EF outbox, architecture §3.3).
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(
            cancellationToken
        );

        var result = await users.ResetPasswordAsync(user, token, Password!);
        if (!result.Succeeded)
        {
            // Identity checks the token before the password, so policy errors are only reachable
            // with a valid token — surfacing them probes nothing. Everything else collapses into
            // the one generic failure.
            if (
                result.Errors.Any(error =>
                    error.Code.StartsWith("Password", StringComparison.Ordinal)
                )
            )
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(nameof(Password), error.Description);
                }

                metrics.PasswordReset("completed", "invalid_password");
                return Page();
            }

            metrics.PasswordReset("completed", "invalid_token");
            Failed = true;
            return Page();
        }

        // The reset proves control of the mailbox, not of existing sessions — whoever holds the
        // old credential's refresh tokens loses them now, and the owner hears about the change.
        await revocation.RevokeAllAsync(user.Id.ToString(), cancellationToken);
        await outbox.PublishAsync(emails.ComposePasswordChanged(user));
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        metrics.PasswordReset("completed", "reset");
        Done = true;
        return Page();
    }
}
