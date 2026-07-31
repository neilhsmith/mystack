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

        // One transaction: the new hash, the revoked grants and the notification email commit
        // together (the EF outbox, architecture §3.3).
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(
            cancellationToken
        );

        var result = await users.ChangePasswordAsync(user, CurrentPassword!, NewPassword!);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            metrics.PasswordChange(
                result.Errors.Any(error =>
                    error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)
                )
                    ? "wrong_current_password"
                    : "invalid_new_password"
            );
            return Page();
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
}
