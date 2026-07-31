using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{EmailOptions.SectionName}:Port must be between 1 and 65535."
            );
        }

        if (options.TimeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{EmailOptions.SectionName}:TimeoutSeconds must be positive."
            );
        }

        // Mailpit is local-only infrastructure (architecture: hosting & deployment). Its SMTP
        // port reaching a hosted environment means every send "succeeds" into an inbox nobody
        // reads — worse than an outage — so the boot refuses instead. Development and the test
        // hosts are the only environments allowed to point at it.
        if (
            options.Port == 1025
            && !builder.Environment.IsDevelopment()
            && !builder.Environment.IsEnvironment("Testing")
        )
        {
            throw new InvalidOperationException(
                $"{EmailOptions.SectionName}:Port is 1025 — Mailpit's SMTP port — outside "
                    + "Development. Hosted environments point at a real provider."
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
