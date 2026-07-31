using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MyStack.Email;

public static class EmailExtensions
{
    /// <summary>
    /// Wires <see cref="IEmailSender"/>: SMTP via MailKit, with every send counted on the
    /// <c>MyStack.Email</c> meter. There is no dev stub and no provider branch — every
    /// environment sends real email (architecture §3.3), locally to compose's Mailpit.
    /// </summary>
    public static WebApplicationBuilder AddEmail(this WebApplicationBuilder builder)
    {
        var options = new EmailOptions();
        builder.Configuration.GetSection(EmailOptions.SectionName).Bind(options);

        // Same rule as connection strings: failing to boot beats silently undeliverable email,
        // and a fallback host would be infrastructure topology compiled into the binary.
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException(
                $"{EmailOptions.SectionName}:Host is not configured."
            );
        }

        if (string.IsNullOrWhiteSpace(options.From))
        {
            throw new InvalidOperationException(
                $"{EmailOptions.SectionName}:From is not configured."
            );
        }

        builder.Services.Configure<EmailOptions>(
            builder.Configuration.GetSection(EmailOptions.SectionName)
        );
        builder.Services.AddSingleton<EmailMetrics>();
        builder.Services.AddSingleton<SmtpEmailSender>();
        builder.Services.AddSingleton<IEmailSender>(provider => new MeteredEmailSender(
            provider.GetRequiredService<SmtpEmailSender>(),
            provider.GetRequiredService<EmailMetrics>()
        ));

        return builder;
    }
}
