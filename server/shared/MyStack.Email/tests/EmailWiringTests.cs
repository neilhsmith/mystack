using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MyStack.Email.Tests;

public sealed class EmailWiringTests
{
    // Same rule as connection strings: an environment without SMTP settings must fail to boot,
    // not quietly hold undeliverable mail.
    [Fact]
    public void A_missing_host_fails_the_boot()
    {
        var builder = Builder(
            new Dictionary<string, string?> { ["Email:From"] = "no-reply@x.test" }
        );

        Should
            .Throw<InvalidOperationException>(() => builder.AddEmail())
            .Message.ShouldContain("Email:Host");
    }

    [Fact]
    public void A_missing_from_address_fails_the_boot()
    {
        var builder = Builder(new Dictionary<string, string?> { ["Email:Host"] = "localhost" });

        Should
            .Throw<InvalidOperationException>(() => builder.AddEmail())
            .Message.ShouldContain("Email:From");
    }

    [Fact]
    public async Task A_configured_host_resolves_the_metered_smtp_sender()
    {
        // Development, because Mailpit's port is only bootable there (the guard below).
        var builder = Builder(
            new Dictionary<string, string?>
            {
                ["Email:Host"] = "localhost",
                ["Email:Port"] = "1025",
                ["Email:From"] = "no-reply@x.test",
            },
            environment: "Development"
        );

        builder.AddEmail();
        await using var app = builder.Build();

        app.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<MeteredEmailSender>();

        var options = app.Services.GetRequiredService<IOptions<EmailOptions>>().Value;
        options.Host.ShouldBe("localhost");
        options.Port.ShouldBe(1025);
    }

    // Mailpit reaching a hosted environment means email silently going nowhere while every send
    // reports success — the boot refuses rather than finding out in production.
    [Fact]
    public void Mailpits_port_outside_development_fails_the_boot()
    {
        var builder = Builder(
            new Dictionary<string, string?>
            {
                ["Email:Host"] = "mailpit",
                ["Email:Port"] = "1025",
                ["Email:From"] = "no-reply@x.test",
            },
            environment: "Production"
        );

        Should
            .Throw<InvalidOperationException>(() => builder.AddEmail())
            .Message.ShouldContain("Mailpit");
    }

    [Fact]
    public void A_nonsense_port_fails_the_boot()
    {
        var builder = Builder(
            new Dictionary<string, string?>
            {
                ["Email:Host"] = "localhost",
                ["Email:Port"] = "0",
                ["Email:From"] = "no-reply@x.test",
            }
        );

        Should
            .Throw<InvalidOperationException>(() => builder.AddEmail())
            .Message.ShouldContain("Email:Port");
    }

    private static WebApplicationBuilder Builder(
        Dictionary<string, string?> settings,
        string? environment = null
    )
    {
        // The environment is explicit because the Mailpit guard branches on it — a test must not
        // depend on whatever ASPNETCORE_ENVIRONMENT the test process happens to carry.
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = environment ?? "Production" }
        );
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
