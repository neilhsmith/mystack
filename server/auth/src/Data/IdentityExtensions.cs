using Microsoft.AspNetCore.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Data;

internal static class IdentityExtensions
{
    public static IServiceCollection AddAuthIdentity(this IServiceCollection services)
    {
        services
            .AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

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
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(cookie => cookie.LoginPath = "/signin");

        return services;
    }
}
