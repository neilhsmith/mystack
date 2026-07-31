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
        var builder = Builder(
            new Dictionary<string, string?>
            {
                ["Email:Host"] = "localhost",
                ["Email:Port"] = "1025",
                ["Email:From"] = "no-reply@x.test",
            }
        );

        builder.AddEmail();
        await using var app = builder.Build();

        app.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<MeteredEmailSender>();

        var options = app.Services.GetRequiredService<IOptions<EmailOptions>>().Value;
        options.Host.ShouldBe("localhost");
        options.Port.ShouldBe(1025);
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
