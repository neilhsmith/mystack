using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MyStack.Auth.Pages;

// The browser-facing face of every empty error response — reached by re-execution from the
// status-code shaping in ErrorPageExtensions, and directly navigable, in which case it still
// answers with the status it names.
public sealed class ErrorModel : PageModel
{
    public int Status { get; private set; }

    public string Title { get; private set; } = "";

    public string Message { get; private set; } = "";

    public string? Detail { get; private set; }

    public void OnGet(int? status)
    {
        Status = status is >= 400 and <= 599 ? status.Value : 500;
        Response.StatusCode = Status;

        (Title, Message) = Status switch
        {
            404 => ("Page not found", "There's nothing at this address."),
            403 => ("Access denied", "Your account doesn't have access to that."),
            429 => ("Too many requests", "Slow down a little, then try again in a minute."),
            _ => ("Something went wrong", "Try again — and if it keeps happening, let us know."),
        };

        // A rejected OIDC request carries a client-visible description worth showing the person
        // stranded mid-flow — it is protocol data the client would have been sent anyway.
        Detail = HttpContext.GetOpenIddictServerResponse()?.ErrorDescription;
    }
}
