using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using MyStack.Auth.Security;
using Shouldly;

namespace MyStack.Auth.Tests;

// Cookie names carry Instance:Name so side-by-side stacks on one host (worktrees, a second
// compose project) keep disjoint browser jars — cookies are host-scoped and port-blind, and
// with per-instance data-protection rings a shared name means each instance evicts the other's
// session instead of merely ignoring it.
public sealed class InstanceCookieTests(AuthAppFixture app)
{
    [Fact]
    public async Task TheDefaultInstance_NamesTheAntiforgeryCookie()
    {
        using var client = app.CreateFlowClient();

        var page = await client.GetAsync("/signin", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();

        page.Headers.GetValues("Set-Cookie")
            .ShouldContain(cookie => cookie.StartsWith(AuthCookies.Antiforgery("mystack") + "="));
    }

    [Fact]
    public async Task ACustomInstanceName_RenamesBothCookies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await app.CreateDatabaseAsync($"instance_{Guid.NewGuid():N}");
        await using var factory = new AuthApplicationFactory(
            database,
            app.BrokerConnectionString,
            settings: new Dictionary<string, string?> { ["Instance:Name"] = "alpha" }
        );

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        client.DefaultRequestHeaders.Add(FakeClientIpStartupFilter.HeaderName, "10.254.0.1");

        var page = await client.GetAsync("/signin", cancellationToken);
        page.EnsureSuccessStatusCode();
        page.Headers.GetValues("Set-Cookie")
            .ShouldContain(cookie => cookie.StartsWith(AuthCookies.Antiforgery("alpha") + "="));

        var email = $"instance-{Guid.NewGuid():N}@example.test";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(
                new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                },
                AuthAppFixture.DefaultPassword
            );
            created.Succeeded.ShouldBeTrue();
        }

        var signedIn = await PageForms.SubmitAsync(
            client,
            "/signin",
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = AuthAppFixture.DefaultPassword,
            },
            cancellationToken
        );

        signedIn.StatusCode.ShouldBe(HttpStatusCode.Found);
        var cookies = signedIn.Headers.GetValues("Set-Cookie").ToArray();
        cookies.ShouldContain(cookie => cookie.StartsWith(AuthCookies.Application("alpha") + "="));

        // Nothing may fall back to a framework default name — that's the cross-instance clobber.
        cookies.ShouldNotContain(cookie => cookie.StartsWith(".AspNetCore."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    public async Task AnUnusableInstanceName_FailsTheBoot(string name)
    {
        await using var factory = new AuthApplicationFactory(
            "Host=localhost;Database=never-reached",
            "amqp://never-reached",
            settings: new Dictionary<string, string?> { ["Instance:Name"] = name }
        );

        Should
            .Throw<InvalidOperationException>(() => factory.CreateClient())
            .Message.ShouldContain("Instance:Name");
    }
}
