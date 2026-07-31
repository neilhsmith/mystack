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
        // Revocation has no mapping on purpose: OpenIddict validates and revokes entirely on its
        // own, and a passthrough handler would have nothing to add.
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync);
        app.MapPost("/connect/token", ExchangeAsync);
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
        AuthDbContext database
    )
    {
        var request =
            context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            // Unreachable while the server config allows exactly two flows; reaching it means
            // the config changed without this handler.
            throw new InvalidOperationException("The grant type is not supported.");
        }

        // The principal stored inside the authorization code or refresh token.
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

    private static async Task<IResult> EndSessionAsync(SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();

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
