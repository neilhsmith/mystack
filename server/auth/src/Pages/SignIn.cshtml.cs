using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Data;
using MyStack.Auth.Telemetry;

namespace MyStack.Auth.Pages;

public sealed class SignInModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
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

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userManager.FindByEmailAsync(Email!);

        if (user is null)
        {
            metrics.SignIn("invalid_credentials");
            return Failed();
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            Password!,
            isPersistent: false,
            lockoutOnFailure: true
        );

        if (result.Succeeded)
        {
            metrics.SignIn("success");

            // Local targets only: ReturnUrl is attacker-writable, and an absolute URL here is a
            // ready-made phishing redirect off a legitimate sign-in.
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }

        metrics.SignIn(
            result switch
            {
                { IsLockedOut: true } => "locked_out",
                { IsNotAllowed: true } => "not_allowed",
                { RequiresTwoFactor: true } => "requires_two_factor",
                _ => "invalid_credentials",
            }
        );

        return Failed();
    }

    private PageResult Failed()
    {
        // One message for every failure — unknown email, wrong password, unconfirmed account,
        // lockout. Anything more specific confirms the account exists (anti-enumeration,
        // architecture §3); the honest outcome goes to the metric tag instead.
        ModelState.AddModelError(string.Empty, "The email or password is incorrect.");
        return Page();
    }
}
