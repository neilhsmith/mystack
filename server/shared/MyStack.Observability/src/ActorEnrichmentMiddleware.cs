using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyStack.Observability;

/// <summary>
/// Emits <c>act.sub</c> on the request span and as a scope on every log line when the principal
/// carries an RFC 8693 <c>act</c> claim — the impersonation seam (architecture §3.2), built once
/// so it never has to be reopened when something starts setting the claim.
/// </summary>
internal sealed class ActorEnrichmentMiddleware(
    RequestDelegate next,
    ILogger<ActorEnrichmentMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var actor = ActorClaim.Sub(context.User);

        if (actor is null)
        {
            await next(context);
            return;
        }

        Activity.Current?.SetTag("act.sub", actor);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["act.sub"] = actor });

        await next(context);
    }
}
