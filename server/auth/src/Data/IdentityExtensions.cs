using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MyStack.Auth.Account;
using MyStack.Auth.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Data;

internal static class IdentityExtensions
{
    public static WebApplicationBuilder AddAuthIdentity(this WebApplicationBuilder builder)
    {
        var instance = builder.Configuration["Instance:Name"] ?? "";
        if (
            instance.Length == 0
            || !instance.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        )
        {
            throw new InvalidOperationException(
                "Instance:Name must be a non-empty token of ASCII letters, digits, '-' or '_'; "
                    + "it suffixes the cookie names that keep side-by-side instances' sessions "
                    + "apart (see AuthCookies)."
            );
        }

        var services = builder.Services;

        services
            .AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Reset tokens get the short-lived provider (PasswordResetTokenProvider);
                // confirmation stays on the default provider's day.
                options.Tokens.PasswordResetTokenProvider =
                    PasswordResetTokenProviderOptions.ProviderName;

                // OIDC claim types instead of Identity's SOAP-era defaults, so the cookie
                // principal and every token OpenIddict mints speak the same names.
                options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
                options.ClaimsIdentity.UserNameClaimType = Claims.Name;
                options.ClaimsIdentity.RoleClaimType = Claims.Role;
                options.ClaimsIdentity.EmailClaimType = Claims.Email;

                // Confirmation is a v1 account flow, so the gate is on from the first account
                // rather than switched on later over users who never went through it.
                options.SignIn.RequireConfirmedEmail = true;

                // NIST SP 800-63B: length carries the strength, and mandatory character classes
                // mostly produce predictable substitutions. Long minimum, no composition rules.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddEntityFrameworkStores<AuthDbContext>()
            // Email-confirmation and password-reset tokens come from these providers. Registering
            // them here means the account flows fail at startup if they ever go missing, not in
            // production at the moment someone asks for a reset.
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>(
                PasswordResetTokenProviderOptions.ProviderName
            );

        // The key ring behind those tokens (and the cookies) persists in the database, so a
        // token emailed before a restart or minted on another replica still verifies. The
        // explicit application name matters: the default derives from the content-root path, and
        // a deploy-path change would silently invalidate every outstanding link.
        services
            .AddDataProtection()
            .PersistKeysToDbContext<AuthDbContext>()
            .SetApplicationName("mystack-auth");

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = AuthCookies.Application(instance);
            cookie.LoginPath = "/signin";
            cookie.AccessDeniedPath = "/access-denied";

            // The session-persistence decision (auth-track 14): the cookie is a browser-session
            // cookie unless the person ticks remember-me; a remembered session lives 14 sliding
            // days — the same order of horizon as a refresh token. Pinned rather than inherited
            // so a framework-default change can't quietly lengthen sessions.
            cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
            cookie.SlidingExpiration = true;
        });

        // Renamed explicitly because the default is not instance-scoped: antiforgery hashes the
        // data-protection application discriminator into its cookie name, and SetApplicationName
        // above pins that — every instance would mint the identical name and clobber each
        // other's in-flight form posts. The external/2FA scheme cookies keep their defaults
        // until something issues them.
        services.AddAntiforgery(options => options.Cookie.Name = AuthCookies.Antiforgery(instance));

        // A password change rotates the security stamp; this is how often other live cookie
        // sessions re-check it. Identity's default half hour is a long ride for a stolen session
        // after the owner rotated the credential.
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5)
        );

        return builder;
    }
}
