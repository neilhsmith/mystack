using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Test authentication handler that issues a <see cref="ClaimsPrincipal"/> from
/// per-request HTTP headers — no token issuance, no PKCE, no cookie. Lets endpoint tests
/// drive any role/scope combination from a single line of test setup:
/// <code>
/// client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, "Admin");
/// client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, "mystack.read mystack.write");
/// </code>
/// Headers omitted → request authenticates as nobody (the handler returns
/// <see cref="AuthenticateResult.NoResult"/> which the authorization layer treats as 401).
/// <para>
/// Registered ONLY in <see cref="ApiTestFactory"/> by replacing the OpenIddict validation
/// scheme handler with this one for the same scheme name. Production wiring is
/// untouched — the handler can't be reached outside tests.
/// </para>
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Comma- or space-separated role names. e.g. <c>"Admin"</c> or <c>"Admin User"</c>.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>Space-separated scope identifiers. e.g. <c>"mystack.read mystack.write"</c>.</summary>
    public const string ScopeHeader = "X-Test-Scope";

    /// <summary>User id (sub claim). Defaults to a synthetic GUID when omitted.</summary>
    public const string SubjectHeader = "X-Test-Sub";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = Context.Request.Headers[RoleHeader].ToString();
        var scopes = Context.Request.Headers[ScopeHeader].ToString();
        var subject = Context.Request.Headers[SubjectHeader].ToString();

        // No headers → no principal. Auth middleware then enforces fallback policy → 401.
        if (string.IsNullOrWhiteSpace(roles) && string.IsNullOrWhiteSpace(scopes))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(OpenIddictConstants.Claims.Subject,
                string.IsNullOrWhiteSpace(subject) ? Guid.CreateVersion7().ToString() : subject),
        };

        foreach (var role in SplitOnAnyOf(roles, ',', ' '))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Role, role));
        }

        foreach (var scope in SplitOnAnyOf(scopes, ' '))
        {
            // OpenIddict's scope claim is emitted one-per-value (see ScopeAuthorizationHandler).
            claims.Add(new Claim(OpenIddictConstants.Claims.Scope, scope));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static IEnumerable<string> SplitOnAnyOf(string value, params char[] separators)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var segment in value.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return segment.Trim();
        }
    }
}
