using System.Net;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.Tests.Integration;

/// <summary>
/// Regression guard for the Swagger UI surface. The UI page itself must serve without
/// any login (the OAuth handshake happens inside the page's Authorize popup, against
/// the protected <c>/connect/*</c> endpoints).
/// <para>
/// The fix being guarded: <c>UseSwaggerUI</c> must run BEFORE
/// <c>UseAuthentication</c> / <c>UseAuthorization</c> in <c>Program.cs</c>. Move it
/// after auth and the fallback policy starts challenging every <c>/swagger/*</c> asset,
/// bouncing the browser to <c>/Account/Login</c> before the UI ever loads — and forcing
/// the developer to log in twice (once for the page, once for the popup).
/// </para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class SwaggerUiTests
{
    private readonly ApiTestFactory _factory;

    public SwaggerUiTests(ApiTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_SwaggerRoot_Redirects_To_IndexHtml_Not_LoginPage()
    {
        // Don't follow the redirect — we want to assert the destination explicitly.
        // Following it would also work because /swagger/index.html serves 200, but
        // a Location header pointing to /Account/Login would silently be followed
        // there too, hiding the bug.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/swagger", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("swagger/index.html", location);
        Assert.DoesNotContain("Account/Login", location);
    }

    [Fact]
    public async Task Get_SwaggerIndexHtml_ServesUiHtml_WithoutLoginChallenge()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Distinguish the real Swagger UI HTML from a login page HTML — the title is set
        // by Program.cs via DocumentTitle.
        Assert.Contains("mystack", body);
        Assert.Contains("swagger-ui", body);
    }

    [Fact]
    public async Task Get_SwaggerOAuth2RedirectHtml_Serves_WithoutLoginChallenge()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(
            "/swagger/oauth2-redirect.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        // The OAuth2 callback page parses the URL fragment + code parameter and posts
        // them back to the parent window. We just need to confirm it's actually served.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("oauth2", body, StringComparison.OrdinalIgnoreCase);
    }
}
