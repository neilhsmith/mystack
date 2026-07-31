using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;
using Wolverine.EntityFrameworkCore;

namespace MyStack.Auth.Pages;

public sealed class ResendConfirmationModel(
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

        // Only an existing, unconfirmed account gets mail; unknown and already-confirmed
        // addresses get the identical page (anti-enumeration), and the honest outcome goes to
        // the metric tag.
        if (user is { EmailConfirmed: false })
        {
            await outbox.PublishAsync(await emails.ComposeConfirmationAsync(user));
            await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
            metrics.EmailConfirmation("resent");
        }
        else
        {
            metrics.EmailConfirmation(
                user is null ? "resend_unknown_email" : "resend_already_confirmed"
            );
        }

        Sent = true;
        return Page();
    }
}
