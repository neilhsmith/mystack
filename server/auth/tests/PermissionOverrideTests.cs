using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Shouldly;

namespace MyStack.Auth.Tests;

// The step-9 proof: override rows reach the access token as `perm`/`perm_deny`, expired rows
// silently don't, and the strings come out exactly as they went in — auth never interprets them.
public sealed class PermissionOverrideTests(AuthAppFixture app)
{
    [Fact]
    public async Task AccessToken_CarriesLiveOverrides_AndOmitsExpired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"override-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        // "literally anything, even spaces" is the never-interprets proof: no catalog exists
        // here, so an arbitrary string must mint verbatim.
        await AddOverrideAsync(
            user.Id,
            "projects:export",
            PermissionOverrideKind.Grant,
            cancellationToken: cancellationToken
        );
        await AddOverrideAsync(
            user.Id,
            "literally anything, even spaces",
            PermissionOverrideKind.Grant,
            cancellationToken: cancellationToken
        );
        await AddOverrideAsync(
            user.Id,
            "users:read",
            PermissionOverrideKind.Deny,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            cancellationToken: cancellationToken
        );
        await AddOverrideAsync(
            user.Id,
            "reports:read",
            PermissionOverrideKind.Grant,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            cancellationToken: cancellationToken
        );

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(client, challenge, "openid", cancellationToken);
        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ClientId,
                ["code_verifier"] = verifier,
            },
            cancellationToken: cancellationToken
        );

        var claims = OAuth.DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!);
        ClaimValues(claims, "perm")
            .ShouldBe(["projects:export", "literally anything, even spaces"], ignoreOrder: true);
        ClaimValues(claims, "perm_deny").ShouldBe(["users:read"]);
        ClaimValues(claims, "perm").ShouldNotContain("reports:read");

        // The identity token describes who the user is, never what they may do — the
        // access-token-only destination in TokenPrincipals.
        var identityClaims = OAuth.DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!);
        identityClaims.TryGetProperty("perm", out _).ShouldBeFalse();
        identityClaims.TryGetProperty("perm_deny", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshedToken_ReflectsTheCurrentStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"override-refresh-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(
            client,
            challenge,
            "openid offline_access",
            cancellationToken
        );
        var tokens = await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = AuthAppFixture.RedirectUri,
                ["client_id"] = AuthAppFixture.ClientId,
                ["code_verifier"] = verifier,
            },
            cancellationToken: cancellationToken
        );

        var claims = OAuth.DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!);
        ClaimValues(claims, "perm").ShouldBeEmpty();

        // Granting after issuance: the principal is rebuilt from the store on refresh, so the
        // next token carries the new claim without a fresh sign-in.
        var granted = await AddOverrideAsync(
            user.Id,
            "projects:export",
            PermissionOverrideKind.Grant,
            cancellationToken: cancellationToken
        );

        var refreshed = await RefreshAsync(
            client,
            tokens.GetProperty("refresh_token").GetString()!,
            cancellationToken
        );
        var refreshedClaims = OAuth.DecodeJwtPayload(
            refreshed.GetProperty("access_token").GetString()!
        );
        ClaimValues(refreshedClaims, "perm").ShouldBe(["projects:export"]);

        // And removing it: gone on the next token — §3.1's revocation-latency bound, bounded by
        // the access-token lifetime rather than the refresh token's horizon.
        await RemoveOverrideAsync(granted.Id, cancellationToken);

        var revoked = await RefreshAsync(
            client,
            refreshed.GetProperty("refresh_token").GetString()!,
            cancellationToken
        );
        var revokedClaims = OAuth.DecodeJwtPayload(
            revoked.GetProperty("access_token").GetString()!
        );
        ClaimValues(revokedClaims, "perm").ShouldBeEmpty();
    }

    // One claim serializes as a JSON string, several as an array — clients see both shapes.
    private static string[] ClaimValues(JsonElement claims, string name)
    {
        if (!claims.TryGetProperty(name, out var property))
        {
            return [];
        }

        return property.ValueKind == JsonValueKind.Array
            ? [.. property.EnumerateArray().Select(value => value.GetString()!)]
            : [property.GetString()!];
    }

    private async Task<PermissionOverride> AddOverrideAsync(
        Guid userId,
        string permission,
        PermissionOverrideKind kind,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var row = new PermissionOverride
        {
            UserId = userId,
            Permission = permission,
            Kind = kind,
            ExpiresAt = expiresAt,
        };
        database.PermissionOverrides.Add(row);
        await database.SaveChangesAsync(cancellationToken);

        return row;
    }

    private async Task RemoveOverrideAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        await database
            .PermissionOverrides.Where(row => row.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<JsonElement> RefreshAsync(
        HttpClient client,
        string refreshToken,
        CancellationToken cancellationToken
    ) =>
        await OAuth.ExchangeAsync(
            client,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = AuthAppFixture.ClientId,
            },
            cancellationToken: cancellationToken
        );
}
