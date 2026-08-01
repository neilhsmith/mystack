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
    // 256 is the schema's varchar bound — without the guard an oversized address dies as a
    // database truncation error, a distinguishable 500 instead of a validation message.
    [BindProperty]
    [Required]
    [EmailAddress]
    [StringLength(256, ErrorMessage = "Email addresses must be 256 characters or fewer.")]
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
        // an existence probe (anti-enumeration, architecture §3). The validators see a prospect
        // rather than null: the built-in validator never reads the user, but a custom one (a
        // password-differs-from-email rule, say) would dereference it.
        var prospect = new ApplicationUser { UserName = Email!, Email = Email! };
        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, prospect, Password!);
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
            // A fresh registration hashes the password on its way into CreateAsync; both
            // existing-account paths hash one for nobody, so response time doesn't separate
            // "new account" from "was already registered".
            Decoys.HashPassword(users.PasswordHasher, Password!);

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
                // The retry path also mints a confirmation token; equalize that too.
                await Decoys.EmailConfirmationTokenAsync(users);
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
