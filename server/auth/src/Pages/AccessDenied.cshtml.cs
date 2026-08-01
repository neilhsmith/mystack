using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MyStack.Auth.Pages;

// Where the cookie handler sends an authenticated user a policy refused — distinct from the 403
// error page because arriving here is a redirect with a live session, not a bare status code.
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() { }
}
