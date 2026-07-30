using System.Net;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class AuthorizationEndpointTests(AuthAppFixture app)
{
    [Fact]
    public async Task Anonymous_IsRedirectedToTheSignInPage()
    {
        using var client = app.CreateFlowClient();
        var (_, challenge) = OAuth.CreatePkcePair();

        var response = await client.GetAsync(
            OAuth.AuthorizeUrl(challenge, "openid"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldContain("/signin?ReturnUrl=");
        // The round trip lands back on the authorization request it interrupted.
        location.ShouldContain(Uri.EscapeDataString("/connect/authorize"));
    }

    [Fact]
    public async Task MissingPkce_IsRejected_BeforeAnyUserInteraction()
    {
        using var client = app.CreateFlowClient();

        var url =
            $"/connect/authorize?client_id={AuthAppFixture.ClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(AuthAppFixture.RedirectUri)}"
            + "&response_type=code&scope=openid&state=xyz";

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        // A direct 400, not an error redirect: nothing travels back to the client's callback,
        // and no sign-in page was ever involved.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("invalid_request");
    }
}
