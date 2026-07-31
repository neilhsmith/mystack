using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class UserInfoTests(AuthAppFixture app)
{
    [Fact]
    public async Task FullScopes_AnswerAgreesWithTheIdToken_AndOmitsPermissions()
    {
        var email = $"userinfo-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email, role: "auditor");
        await AddOverrideAsync(user.Id, "projects:export");

        var tokens = await TokensAsync(
            "openid email profile roles api.read",
            email,
            TestContext.Current.CancellationToken
        );
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // The override reached the access token — proving its absence below means something.
        OAuth.DecodeJwtPayload(accessToken).TryGetProperty("perm", out _).ShouldBeTrue();

        var (status, userinfo) = await UserInfoAsync(
            accessToken,
            TestContext.Current.CancellationToken
        );
        status.ShouldBe(HttpStatusCode.OK);

        // Exactly the identity claims, nothing else — no perm, no protocol plumbing.
        userinfo
            .EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["sub", "name", "email", "role"], ignoreOrder: true);
        userinfo.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        userinfo.GetProperty("email").GetString().ShouldBe(email);
        userinfo.GetProperty("name").GetString().ShouldBe(email);
        userinfo.GetProperty("role").GetString().ShouldBe("auditor");

        // The step-10 contract: the id token and userinfo agree, claim for claim.
        var identityClaims = OAuth.DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!);
        foreach (var name in new[] { "sub", "name", "email", "role" })
        {
            identityClaims
                .GetProperty(name)
                .GetString()
                .ShouldBe(userinfo.GetProperty(name).GetString(), $"claim '{name}'");
        }

        identityClaims.TryGetProperty("perm", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task NarrowScope_AnswersOnlyWhatWasGranted()
    {
        var email = $"userinfo-narrow-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email, role: "auditor");

        var tokens = await TokensAsync(
            "openid email",
            email,
            TestContext.Current.CancellationToken
        );

        var (status, userinfo) = await UserInfoAsync(
            tokens.GetProperty("access_token").GetString()!,
            TestContext.Current.CancellationToken
        );
        status.ShouldBe(HttpStatusCode.OK);

        // profile and roles were not granted, so name and role do not exist here — not even
        // as empty values. The role is in the access token regardless; userinfo is gated.
        userinfo
            .EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["sub", "email"], ignoreOrder: true);
        userinfo.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        userinfo.GetProperty("email").GetString().ShouldBe(email);
    }

    [Fact]
    public async Task WithoutAToken_IsUnauthorized()
    {
        var response = await app.Client.GetAsync(
            "/connect/userinfo",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<JsonElement> TokensAsync(
        string scope,
        string email,
        CancellationToken cancellationToken
    )
    {
        using var client = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        var (verifier, challenge) = OAuth.CreatePkcePair();
        var code = await OAuth.AuthorizeAsync(client, challenge, scope, cancellationToken);

        return await OAuth.ExchangeAsync(
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
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> UserInfoAsync(
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await app.Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return (
            response.StatusCode,
            body.Length > 0 ? JsonDocument.Parse(body).RootElement.Clone() : default
        );
    }

    private async Task AddOverrideAsync(Guid userId, string permission)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        database.PermissionOverrides.Add(
            new PermissionOverride
            {
                UserId = userId,
                Permission = permission,
                Kind = PermissionOverrideKind.Grant,
            }
        );
        await database.SaveChangesAsync();
    }
}
