using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyStack.Observability;

/// <summary>
/// Emits the authenticated principal's <c>sub</c> on the request span and as a scope on every
/// log line — and <c>act.sub</c> beside it when the principal carries an RFC 8693 <c>act</c>
/// claim, the impersonation seam (architecture §3.2), built once so it never has to be reopened
/// when something starts setting that claim. The subject claim type is the OIDC <c>sub</c>,
/// which is what this stack's hosts put on their principals (auth maps Identity's claim types
/// to it; a resource server keeps it by not remapping inbound JWT claims).
/// </summary>
internal sealed class ActorEnrichmentMiddleware(
    RequestDelegate next,
    ILogger<ActorEnrichmentMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var subject = context.User.Identity?.IsAuthenticated is true
            ? context.User.FindFirst("sub")?.Value
            : null;
        var actor = ActorClaim.Sub(context.User);

        if (subject is null && actor is null)
        {
            await next(context);
            return;
        }

        var tags = new Dictionary<string, object>(capacity: 2);
        if (subject is not null)
        {
            Activity.Current?.SetTag("sub", subject);
            tags["sub"] = subject;
        }

        if (actor is not null)
        {
            Activity.Current?.SetTag("act.sub", actor);
            tags["act.sub"] = actor;
        }

        using var scope = logger.BeginScope(tags);

        await next(context);
    }
}
