using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Primitives;
using MyStack.Auth.Data;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

internal static class OidcEndpoints
{
    public static WebApplication MapAuthOidcEndpoints(this WebApplication app)
    {
        // Revocation, introspection, device authorization and PAR have no mapping on purpose:
        // OpenIddict handles all four entirely on its own, and a passthrough handler would have
        // nothing to add. The end-user verification endpoint is the Verify Razor page — it
        // renders forms, so it lives with the other pages.
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync);
        app.MapPost("/connect/token", ExchangeAsync);
        app.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync);
        app.MapMethods("/connect/endsession", [HttpMethods.Get, HttpMethods.Post], EndSessionAsync);

        return app;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AuthDbContext database,
        IOpenIddictApplicationManager applicationManager
    )
    {
        var request =
            context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = result.Succeeded ? await userManager.GetUserAsync(result.Principal) : null;

        if (
            user is null
            || request.HasPromptValue(PromptValues.Login)
            || (
                request.MaxAge is { } maxAge
                && result.Properties?.IssuedUtc is { } issued
                && DateTimeOffset.UtcNow - issued > TimeSpan.FromSeconds(maxAge)
            )
        )
        {
            // prompt=none is the client promising no interaction; honoring it means answering
            // with an error rather than a sign-in page.
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "The user is not signed in.");
            }

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrl(request, context.Request) },
                [IdentityConstants.ApplicationScheme]
            );
        }

        var application =
            await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The validated client cannot be retrieved.");

        // There is no consent screen (architecture D17): every v1 client is first-party and
        // registered with implicit consent. A client registered otherwise is a configuration
        // error, surfaced as the error a compliant client understands.
        if (!await applicationManager.HasConsentTypeAsync(application, ConsentTypes.Implicit))
        {
            return Forbid(
                Errors.ConsentRequired,
                "Only clients registered with implicit consent are supported."
            );
        }

        var principal = await TokenPrincipals.CreateAsync(
            signInManager,
            database,
            user,
            request.GetScopes()
        );

        return Results.SignIn(
            principal,
            properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AuthDbContext database,
        IOpenIddictApplicationManager applicationManager
    )
    {
        var request =
            context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            // OpenIddict already authenticated the client and checked its grant-type and scope
            // permissions — only a confidential client with valid credentials reaches here.
            var application =
                await applicationManager.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The validated client cannot be retrieved.");

            return Results.SignIn(
                TokenPrincipals.CreateForClient(
                    request.ClientId!,
                    await applicationManager.GetDisplayNameAsync(application),
                    request.GetScopes()
                ),
                properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
            );
        }

        if (
            !request.IsAuthorizationCodeGrantType()
            && !request.IsRefreshTokenGrantType()
            && !request.IsDeviceCodeGrantType()
        )
        {
            // Unreachable while the server config allows exactly four flows; reaching it means
            // the config changed without this handler.
            throw new InvalidOperationException("The grant type is not supported.");
        }

        // The principal stored inside the authorization code, refresh token or device code —
        // for a device code, the one the verification page attached when the user approved.
        var result = await context.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );

        var subject = result.Principal?.GetClaim(Claims.Subject);
        var user = subject is null ? null : await userManager.FindByIdAsync(subject);

        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return Forbid(Errors.InvalidGrant, "The token is no longer valid.");
        }

        // Rebuilt from the store rather than copied from the incoming token, so role, email and
        // override changes take effect on the next refresh instead of surviving to the token's
        // horizon.
        var principal = await TokenPrincipals.CreateAsync(
            signInManager,
            database,
            user,
            result.Principal!.GetScopes()
        );

        return Results.SignIn(
            principal,
            properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
    }

    private static async Task<IResult> UserInfoAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AuthDbContext database
    )
    {
        // The principal from the access token the caller presented — OpenIddict validated the
        // token before passthrough.
        var result = await context.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );

        // TryParse before the store lookup: a machine token's sub is its client id, which
        // Identity would throw on converting to a key — the right answer is invalid_token,
        // because there is no user behind that token to describe.
        var subject = result.Principal?.GetClaim(Claims.Subject);
        var user = Guid.TryParse(subject, out _) ? await userManager.FindByIdAsync(subject!) : null;
        if (user is null)
        {
            return Challenge(Errors.InvalidToken, "The access token has no user behind it.");
        }

        // Rebuilt through the same funnel token issuance uses and filtered to the claims whose
        // destination includes the identity token, so userinfo and the id token agree by
        // construction — same scope gating, and perm/perm_deny stay out of both.
        var principal = await TokenPrincipals.CreateAsync(
            signInManager,
            database,
            user,
            result.Principal!.GetScopes()
        );

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = subject!,
        };

        foreach (
            var group in principal
                .Claims.Where(claim => claim.GetDestinations().Contains(Destinations.IdentityToken))
                .GroupBy(claim => claim.Type)
        )
        {
            // One value serializes as a JSON string, several as an array — the same shape rule
            // the JWTs follow.
            claims[group.Key] =
                group.Count() == 1
                    ? group.First().Value
                    : group.Select(claim => claim.Value).ToArray();
        }

        return Results.Ok(claims);
    }

    private static async Task<IResult> EndSessionAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        BackchannelLogoutNotifier backchannelLogout
    )
    {
        // Whose session is ending: the live cookie's user, or — when the cookie already
        // expired — the subject of the id_token_hint OpenIddict validated, so a client-initiated
        // sign-out still propagates to the other apps holding their own sessions. Neither means
        // there is nobody to notify about.
        var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var subject = cookie.Succeeded ? userManager.GetUserId(cookie.Principal) : null;
        if (subject is null)
        {
            var hint = await context.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
            );
            subject = hint.Principal?.GetClaim(Claims.Subject);
        }

        await signInManager.SignOutAsync();

        if (subject is not null)
        {
            await backchannelLogout.NotifyAsync(
                subject,
                new Uri(
                    $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}",
                    UriKind.Absolute
                )
            );
        }

        // OpenIddict redirects to the request's post_logout_redirect_uri when it validated one;
        // RedirectUri is only the fallback for a bare sign-out.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );
    }

    private static IResult Forbid(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(
                new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
                }
            ),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );

    private static IResult Challenge(string error, string description) =>
        Results.Challenge(
            new AuthenticationProperties(
                new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
                }
            ),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );

    private static string ReturnUrl(OpenIddictRequest request, HttpRequest httpRequest)
    {
        // The URL the sign-in page returns to is this request, minus prompt=login — otherwise
        // the round trip back here would demand a fresh sign-in forever.
        var prompt = string.Join(' ', request.GetPromptValues().Remove(PromptValues.Login));

        var parameters = httpRequest.HasFormContentType
            ? httpRequest.Form.Where(parameter => parameter.Key != Parameters.Prompt).ToList()
            : httpRequest.Query.Where(parameter => parameter.Key != Parameters.Prompt).ToList();

        if (!string.IsNullOrEmpty(prompt))
        {
            parameters.Add(new(Parameters.Prompt, new StringValues(prompt)));
        }

        return httpRequest.PathBase + httpRequest.Path + QueryString.Create(parameters);
    }
}
