using System.Net;
using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

// The error surface splits on the Accept header: a navigating browser gets the error page with
// the status intact, an API caller gets ProblemDetails — and nothing anywhere gets a raw blank
// response.
public sealed class ErrorPageTests(AuthAppFixture app)
{
    [Fact]
    public async Task AMissingPath_AnswersABrowser_WithThe404Page()
    {
        var response = await GetAsHtmlAsync(
            $"/nothing-here-{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        // Razor encodes the apostrophe in "There's", so pin the unambiguous fragment.
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("nothing at this address.");
    }

    [Fact]
    public async Task AMissingPath_AnswersAnApiCaller_WithProblemDetails()
    {
        using var client = app.CreateFlowClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        var response = await client.GetAsync(
            $"/nothing-here-{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonDocument.Parse(body).RootElement.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact]
    public async Task AnUnhandledException_AnswersABrowser_WithTheErrorPage_NotTheDetails()
    {
        var response = await GetAsHtmlAsync("/debug/throw", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(500);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Something went wrong");
        html.ShouldNotContain("Deliberate test failure");
    }

    // A rejected OIDC request strands a person mid-flow with no client to return to; the error
    // page is what they see, carrying the protocol's own description of what went wrong.
    [Fact]
    public async Task ARejectedAuthorizeRequest_AnswersABrowser_WithTheErrorPage()
    {
        var (_, challenge) = OAuth.CreatePkcePair();
        var response = await GetAsHtmlAsync(
            OAuth.AuthorizeUrl(challenge, "openid", clientId: "no-such-client"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Something went wrong");
        // The OIDC error description rides along — protocol data the client would have been
        // sent anyway.
        html.ShouldContain("client");
    }

    // Directly navigable, and honest about its status when it is.
    [Fact]
    public async Task TheErrorPage_AnswersWithTheStatusItNames()
    {
        var response = await GetAsHtmlAsync("/error/404", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        ).ShouldContain("Page not found");
    }

    [Fact]
    public async Task ANonsenseStatus_CollapsesToTheGeneric500()
    {
        var response = await GetAsHtmlAsync("/error/999", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(500);
        (
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        ).ShouldContain("Something went wrong");
    }

    private async Task<HttpResponseMessage> GetAsHtmlAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        using var client = app.CreateFlowClient();
        client.DefaultRequestHeaders.Add(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
        );
        return await client.GetAsync(url, cancellationToken);
    }
}
