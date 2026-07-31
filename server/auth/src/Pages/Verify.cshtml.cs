using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Data;
using MyStack.Auth.Oidc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Pages;

// The device flow's human half (RFC 8628): the device shows a short user code and polls the
// token endpoint; the user brings the code here — a real browser, a real sign-in — and approves
// or denies. Signed-in is a precondition rather than a step: the approval binds the device's
// tokens to whoever approves, so the page must know who that is before showing anything.
[Authorize]
public sealed class VerifyModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    AuthDbContext database,
    IOpenIddictApplicationManager applicationManager
) : PageModel
{
    public string? UserCode { get; private set; }

    public string? ClientName { get; private set; }

    public IReadOnlyList<string> Scopes { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Done { get; private set; }

    public async Task OnGetAsync([FromQuery] string? done)
    {
        // The redirect target of a completed approve/deny — a plain confirmation render, no
        // OpenIddict state left to consult.
        if (done is "approved" or "denied")
        {
            Done = done;
            return;
        }

        var request =
            HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        // No code yet: render the entry form. The form submits with GET on purpose — it lands
        // back here exactly like a `verification_uri_complete` link from the device, so both
        // entry paths are one code path.
        if (string.IsNullOrEmpty(request.UserCode))
        {
            return;
        }

        // AuthenticateAsync against the OpenIddict scheme is what validates the user code and
        // returns the principal stored when the device asked to be authorized. An unknown code
        // can still "succeed" with an empty principal, so the client_id claim is the real test.
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
        var clientId = result.Succeeded ? result.Principal.GetClaim(Claims.ClientId) : null;
        if (string.IsNullOrEmpty(clientId))
        {
            Error = CodeNotRecognized;
            return;
        }

        var application = await applicationManager.FindByClientIdAsync(clientId);

        UserCode = request.UserCode;
        ClientName = application is null
            ? null
            : await applicationManager.GetDisplayNameAsync(application);
        Scopes = result.Principal!.GetScopes();
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            // A cookie that no longer maps to a user re-authenticates rather than 500s.
            return Challenge();
        }

        // Re-validated from the posted user_code — the hidden field, not trust in the earlier
        // GET — so an expired or already-redeemed code fails here too. Same empty-principal
        // caveat as the GET: the client_id claim is what proves the code resolved.
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
        if (!result.Succeeded || result.Principal.GetClaim(Claims.ClientId) is null or "")
        {
            Error = CodeNotRecognized;
            return Page();
        }

        // The same funnel every user token goes through — roles, permission overrides,
        // destinations — with the scopes the device asked for. OpenIddict attaches this
        // principal to the device code, and the device's next poll becomes tokens.
        var principal = await TokenPrincipals.CreateAsync(
            signInManager,
            database,
            user,
            result.Principal!.GetScopes()
        );

        return SignIn(
            principal,
            new AuthenticationProperties { RedirectUri = "/connect/verify?done=approved" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
    }

    public IActionResult OnPostDeny() =>
        // Forbid against the OpenIddict scheme rejects the posted user code outright — the
        // device's next poll gets access_denied instead of pending-forever.
        Forbid(
            new AuthenticationProperties(
                new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "The user denied the request.",
                }
            )
            {
                RedirectUri = "/connect/verify?done=denied",
            },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );

    // One message for every bad code — mistyped, expired, already used. Which one it was is
    // nothing the person at the keyboard can act on differently.
    private const string CodeNotRecognized =
        "That code wasn't recognized. Check the device and try again — codes expire after a few minutes.";
}
