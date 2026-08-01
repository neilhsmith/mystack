using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MyStack.Auth.Data;
using MyStack.Auth.Oidc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Pages;

// The end-session endpoint (RP-initiated logout), as a page because sometimes it must render
// one: a request carrying a validated id_token_hint is the client proving the sign-out is its
// own doing and is honored without a prompt, but anything less could be a single forced
// navigation — and one navigation must not end every app's session. Those confirm first.
//
// Antiforgery is validated by hand rather than by the page filter: a client's form_post logout
// request is a legitimate cross-site POST, so the filter would reject exactly the callers the
// endpoint exists for. The hint takes the token's place as proof; only a hint-less confirmation
// POST needs the token.
[IgnoreAntiforgeryToken]
public sealed class EndSessionModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    BackchannelLogoutNotifier backchannelLogout,
    IAntiforgery antiforgery
) : PageModel
{
    public IReadOnlyList<(string Name, string Value)> Parameters { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await HintPrincipalAsync() is not null)
        {
            return await SignOutEverywhereAsync();
        }

        CollectParameters();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (
            await HintPrincipalAsync() is null
            && !await antiforgery.IsRequestValidAsync(HttpContext)
        )
        {
            // A cross-site POST without proof is the forced sign-out the confirmation exists to
            // stop; re-render it — with a fresh token — rather than act.
            CollectParameters();
            return Page();
        }

        return await SignOutEverywhereAsync();
    }

    private async Task<IActionResult> SignOutEverywhereAsync()
    {
        // Whose session is ending: the live cookie's user, or — when the cookie already
        // expired — the subject of the validated id_token_hint, so a client-initiated sign-out
        // still propagates to the other apps holding their own sessions. Neither means there is
        // nobody to notify about.
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var subject = cookie.Succeeded ? userManager.GetUserId(cookie.Principal) : null;
        subject ??= (await HintPrincipalAsync())?.GetClaim(Claims.Subject);

        await signInManager.SignOutAsync();

        if (subject is not null)
        {
            await backchannelLogout.NotifyAsync(
                subject,
                new Uri($"{Request.Scheme}://{Request.Host}{Request.PathBase}", UriKind.Absolute)
            );
        }

        // OpenIddict redirects to the request's post_logout_redirect_uri when it validated one;
        // RedirectUri is only the fallback for a bare sign-out.
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/signed-out" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
    }

    // Non-null exactly when the request carried an id_token_hint OpenIddict validated — an
    // invalid hint was already rejected before the passthrough reached this page. Same caveat
    // as the Verify page's user code: authenticate can "succeed" with an empty principal when
    // no hint was sent at all, so the subject claim is the real test. The handler caches per
    // request, so asking twice costs one authentication.
    private async Task<ClaimsPrincipal?> HintPrincipalAsync()
    {
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );

        return result.Principal?.GetClaim(Claims.Subject) is null ? null : result.Principal;
    }

    private void CollectParameters()
    {
        var request =
            HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        // The validated request, echoed through the confirmation form so the POST is the same
        // logout request — OpenIddict re-validates it, and a registered post_logout_redirect_uri
        // still gets its redirect after the person agrees.
        Parameters = request
            .GetParameters()
            .Select(parameter => (parameter.Key, Value: (string?)parameter.Value))
            .Where(parameter => !string.IsNullOrEmpty(parameter.Value))
            .Select(parameter => (parameter.Key, parameter.Value!))
            .ToList();
    }
}
