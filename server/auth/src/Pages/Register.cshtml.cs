using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;
using Wolverine.EntityFrameworkCore;

namespace MyStack.Auth.Pages;

public sealed class RegisterModel(
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

    [BindProperty]
    [Required]
    public string? Password { get; set; }

    [BindProperty]
    [Required]
    [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    public string? ConfirmPassword { get; set; }

    public bool Registered { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Password policy before the existence lookup, so a rejected password reads the same
        // whether or not the address has an account — otherwise a deliberately weak password is
        // an existence probe (anti-enumeration, architecture §3).
        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, null!, Password!);
            if (!validation.Succeeded)
            {
                foreach (var error in validation.Errors)
                {
                    ModelState.AddModelError(nameof(Password), error.Description);
                }
            }
        }

        if (!ModelState.IsValid)
        {
            metrics.Registration("invalid_password");
            return Page();
        }

        // One transaction under the whole flow: the user row and the outgoing email envelope
        // commit together or not at all (the EF outbox, architecture §3.3).
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(
            cancellationToken
        );

        var existing = await users.FindByEmailAsync(Email!);
        if (existing is not null)
        {
            // The page answer stays indistinguishable from a fresh registration. An unconfirmed
            // account gets its confirmation again — the "I registered but never clicked the
            // link" retry; a confirmed one gets nothing.
            if (!existing.EmailConfirmed)
            {
                await outbox.PublishAsync(await emails.ComposeConfirmationAsync(existing));
                await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
                metrics.Registration("resent_confirmation");
            }
            else
            {
                metrics.Registration("already_registered");
            }

            Registered = true;
            return Page();
        }

        var user = new ApplicationUser { UserName = Email, Email = Email };
        var created = await users.CreateAsync(user, Password!);

        if (!created.Succeeded)
        {
            // The branches above caught everything knowable; what's left is a race or an edge
            // Identity rejects, and distinguishing it would leak. Collapse to the generic page.
            metrics.Registration("failed");
            Registered = true;
            return Page();
        }

        await outbox.PublishAsync(await emails.ComposeConfirmationAsync(user));
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        metrics.Registration("created");
        Registered = true;
        return Page();
    }
}
