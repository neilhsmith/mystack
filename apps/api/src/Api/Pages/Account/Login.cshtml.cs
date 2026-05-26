using Api.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Api.Pages.Account;

/// <summary>
/// Sign-in page for the interactive (authorization-code + PKCE) flow. The OpenIddict
/// authorize endpoint challenges this scheme when the user isn't already authenticated;
/// after sign-in the user is redirected back via the <see cref="ReturnUrl"/> set by
/// <c>AuthenticationProperties.RedirectUri</c> upstream.
/// </summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        // If we're already signed in, jump straight to the return URL. Skip the form.
        if (User.Identity?.IsAuthenticated == true && IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl!);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Email and password are required.";
            return Page();
        }

        // Identity defaults: lockoutOnFailure: true (5 attempts = 5 min lockout per
        // appsettings). We surface a single generic error message for any failure so the
        // page doesn't double as a user-enumeration oracle.
        var result = await _signInManager.PasswordSignInAsync(
            Email,
            Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            ErrorMessage = "This account is temporarily locked. Try again later.";
            return Page();
        }

        if (!result.Succeeded)
        {
            ErrorMessage = "Invalid email or password.";
            return Page();
        }

        if (IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl!);
        }

        return Redirect("/");
    }

    private bool IsLocalUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Url.IsLocalUrl(url);
}
