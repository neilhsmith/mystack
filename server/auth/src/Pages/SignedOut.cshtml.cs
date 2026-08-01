using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MyStack.Auth.Pages;

// The end-session fallback: where a sign-out lands when no validated post_logout_redirect_uri
// says otherwise. Static on purpose — by the time anyone is here, there is no session to consult.
public sealed class SignedOutModel : PageModel
{
    public void OnGet() { }
}
