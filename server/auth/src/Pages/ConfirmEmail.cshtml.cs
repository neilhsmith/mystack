using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;

namespace MyStack.Auth.Pages;

// The emailed link lands on the GET, which renders a form and changes nothing — so a mailbox
// link-scanner prefetching the URL can't consume the single-use token. The button's POST is what
// confirms (auth-track step 8). The GET also issues the antiforgery cookie, so the URL works
// pasted into any browser at any later time.
public sealed class ConfirmEmailModel(UserManager<ApplicationUser> users, AuthMetrics metrics)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool Confirmed { get; private set; }

    public bool AlreadyConfirmed { get; private set; }

    public bool Failed { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = Guid.TryParse(UserId, out _) ? await users.FindByIdAsync(UserId!) : null;

        // Unknown user and undecodable token collapse into the one generic failure — this page
        // is reachable with arbitrary query values, so it anti-enumerates like everything else.
        if (user is null || !AccountTokens.TryDecode(Token, out var token))
        {
            metrics.EmailConfirmation(user is null ? "unknown_user" : "invalid_token");
            Failed = true;
            return Page();
        }

        if (user.EmailConfirmed)
        {
            // The boring branch users actually hit: clicking the link twice. Reaching it took a
            // valid user id from a genuine email, so saying so leaks nothing.
            metrics.EmailConfirmation("already_confirmed");
            AlreadyConfirmed = true;
            return Page();
        }

        var result = await users.ConfirmEmailAsync(user, token);
        if (result.Succeeded)
        {
            metrics.EmailConfirmation("confirmed");
            Confirmed = true;
        }
        else
        {
            metrics.EmailConfirmation("invalid_token");
            Failed = true;
        }

        return Page();
    }
}
