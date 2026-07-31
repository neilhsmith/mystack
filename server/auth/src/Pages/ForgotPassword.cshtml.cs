using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;
using Wolverine.EntityFrameworkCore;

namespace MyStack.Auth.Pages;

public sealed class ForgotPasswordModel(
    UserManager<ApplicationUser> users,
    AccountEmails emails,
    IDbContextOutbox<AuthDbContext> outbox,
    AuthMetrics metrics
) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string? Email { get; set; }

    public bool Sent { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await users.FindByEmailAsync(Email!);

        // Reset links go to confirmed addresses only: unconfirmed means the address was never
        // proven to belong to whoever registered it, and a reset link would hand the account to
        // the address's real owner-of-the-moment while leapfrogging confirmation. Unknown and
        // unconfirmed get the identical page (anti-enumeration); the metric tag stays honest.
        if (user is { EmailConfirmed: true })
        {
            await outbox.PublishAsync(await emails.ComposePasswordResetAsync(user));
            await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
            metrics.PasswordReset("requested", "sent");
        }
        else
        {
            metrics.PasswordReset(
                "requested",
                user is null ? "unknown_email" : "unconfirmed_email"
            );
        }

        Sent = true;
        return Page();
    }
}
