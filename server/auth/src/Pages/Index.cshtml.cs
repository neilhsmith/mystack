using Microsoft.AspNetCore.Mvc.RazorPages;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Pages;

// The default post-sign-in target and the end-session fallback. Nobody is sent here by a client
// flow — those return to the client's own redirect — so all it owes anyone is orientation: who
// you are, and the account actions this host owns.
public sealed class IndexModel : PageModel
{
    public string? Email { get; private set; }

    public void OnGet()
    {
        if (User.Identity?.IsAuthenticated is true)
        {
            Email = User.FindFirst(Claims.Email)?.Value ?? User.Identity.Name;
        }
    }
}
