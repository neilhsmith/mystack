using Shouldly;

namespace MyStack.Auth.Tests;

// "Security headers on every response" is a non-negotiable — so the full set is pinned here,
// not just the one CSP directive the sign-in tests check. Removing the middleware, or loosening
// a directive, must fail a test.
public sealed class SecurityHeaderTests(AuthAppFixture app)
{
    [Fact]
    public async Task EveryResponse_CarriesTheLockedDefaultPolicy()
    {
        // The discovery document: a JSON endpoint no page policy touches.
        var response = await app.Client.GetAsync(
            "/.well-known/openid-configuration",
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        AssertCommonHeaders(response);
        var csp = Csp(response);
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("base-uri 'none'");
        csp.ShouldContain("form-action 'none'");
    }

    [Fact]
    public async Task RenderedPages_LoosenExactlyFormAction()
    {
        var response = await app.Client.GetAsync("/signin", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        AssertCommonHeaders(response);
        var csp = Csp(response);
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("base-uri 'none'");
        csp.ShouldContain("form-action 'self'");
    }

    private static void AssertCommonHeaders(HttpResponseMessage response)
    {
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "Cross-Origin-Resource-Policy").ShouldBe("same-origin");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        Header(response, "Cross-Origin-Embedder-Policy").ShouldBe("require-corp");
        response.Headers.Contains("Permissions-Policy").ShouldBeTrue();

        // The library emits HSTS on https only, and the test host is http — absence here pins
        // that the header is the library's call, not hand-rolled somewhere it would double up.
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();

        // Kestrel's Server header is suppressed: naming the server buys a scanner a head start.
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    private static string Header(HttpResponseMessage response, string name) =>
        string.Join(' ', response.Headers.GetValues(name));

    private static string Csp(HttpResponseMessage response) =>
        string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
}
