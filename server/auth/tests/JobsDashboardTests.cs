using System.Net;
using MyStack.Auth.Security;
using Shouldly;

namespace MyStack.Auth.Tests;

// The dashboard can requeue and delete jobs, so its gate gets the same matrix a protected
// endpoint would: anonymous, authenticated-but-unauthorized, authorized.
public sealed class JobsDashboardTests(AuthAppFixture app)
{
    [Fact]
    public async Task Anonymous_IsSentToSignIn()
    {
        using var client = app.CreateFlowClient();

        var response = await client.GetAsync("/jobs", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location.ShouldNotBeNull();
        location.AbsolutePath.ShouldBe("/signin");
        location.Query.ShouldContain("ReturnUrl=%2Fjobs");
    }

    [Fact]
    public async Task SignedInWithoutAdminRole_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"dashboard-user-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync("/jobs", cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_SeesTheDashboard()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"dashboard-admin-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, role: AuthRoles.Admin);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var response = await client.GetAsync("/jobs", cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("Hangfire");

        // The dashboard carries its own named header policy — script-src exists only there,
        // so its presence proves the endpoint isn't running under the default deny-all CSP
        // (which would blank the UI) or the pages policy.
        var csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        csp.ShouldContain("script-src 'self'");
        csp.ShouldContain("style-src 'self' 'unsafe-inline'");
    }
}
