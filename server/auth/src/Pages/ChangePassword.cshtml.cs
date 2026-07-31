using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Oidc;
using MyStack.Auth.Telemetry;
using Wolverine.EntityFrameworkCore;

namespace MyStack.Auth.Pages;

// The one signed-in account page. The caller already authenticated, so unlike the anonymous
// flows this one answers specifically — "the current password is incorrect" reveals nothing to
// someone who is that user's cookie.
[Authorize]
public sealed class ChangePasswordModel(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    AccountEmails emails,
    TokenRevocationService revocation,
    IDbContextOutbox<AuthDbContext> outbox,
    AuthMetrics metrics
) : PageModel
{
    [BindProperty]
    [Required]
    public string? CurrentPassword { get; set; }

    [BindProperty]
    [Required]
    public string? NewPassword { get; set; }

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
    public string? ConfirmPassword { get; set; }

    public bool Changed { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            // A cookie that no longer maps to a user re-authenticates rather than 500s.
            return Challenge();
        }

        // This form verifies the current password, which makes it a guessing surface for anyone
        // holding a hijacked cookie — ChangePasswordAsync never touches the lockout counters on
        // its own, so the same lockout that guards the sign-in page is enforced by hand here.
        // The honest message is fine: the caller is already this account's session.
        if (await users.IsLockedOutAsync(user))
        {
            ModelState.AddModelError(string.Empty, LockedOutMessage);
            metrics.PasswordChange("locked_out");
            return Page();
        }

        // One transaction: the new hash, the revoked grants and the notification email commit
        // together (the EF outbox, architecture §3.3).
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(
            cancellationToken
        );

        var result = await users.ChangePasswordAsync(user, CurrentPassword!, NewPassword!);
        if (!result.Succeeded)
        {
            var mismatch = result.Errors.Any(error =>
                error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)
            );
            if (mismatch)
            {
                // The failure count must persist while the transaction — which only ever
                // commits a successful change — rolls back, so end it first.
                await transaction.RollbackAsync(cancellationToken);
                await users.AccessFailedAsync(user);
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            metrics.PasswordChange(mismatch ? "wrong_current_password" : "invalid_new_password");
            return Page();
        }

        if (user.AccessFailedCount > 0)
        {
            await users.ResetAccessFailedCountAsync(user);
        }

        await revocation.RevokeAllAsync(user.Id.ToString(), cancellationToken);
        await outbox.PublishAsync(emails.ComposePasswordChanged(user));
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        // ChangePasswordAsync rotated the security stamp, which is what signs other cookie
        // sessions out at their next stamp check — refresh this one so the browser that made the
        // change stays signed in.
        await signInManager.RefreshSignInAsync(user);

        metrics.PasswordChange("changed");
        Changed = true;
        return Page();
    }

    private const string LockedOutMessage =
        "Too many incorrect attempts. Try again in a few minutes.";
}
