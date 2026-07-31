using MyStack.Auth.Oidc;
using MyStack.Email;

namespace MyStack.Auth.Account;

internal static class AccountExtensions
{
    public static WebApplicationBuilder AddAccountFlows(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AccountOptions.SectionName);
        var options = new AccountOptions();
        section.Bind(options);

        // Validated eagerly: a wrong link base doesn't fail until someone's confirmation email
        // has already gone out, so failing to boot is the only honest moment. The scheme check
        // matters on Unix, where Uri.TryCreate accepts a rooted path like "/auth" as an absolute
        // file: URI.
        if (
            !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new InvalidOperationException(
                "Account:PublicBaseUrl must be an absolute http(s) URL; "
                    + "emailed confirm/reset links are built from it."
            );
        }

        builder.Services.Configure<AccountOptions>(section);

        builder.Services.AddSingleton<
            IEmailRenderer<ConfirmationEmail>,
            ConfirmationEmailRenderer
        >();
        builder.Services.AddSingleton<
            IEmailRenderer<PasswordResetEmail>,
            PasswordResetEmailRenderer
        >();
        builder.Services.AddSingleton<
            IEmailRenderer<PasswordChangedEmail>,
            PasswordChangedEmailRenderer
        >();

        builder.Services.AddScoped<AccountEmails>();
        builder.Services.AddScoped<TokenRevocationService>();

        return builder;
    }
}
