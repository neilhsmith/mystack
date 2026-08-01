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

        // Exactly the identity claims, nothing else — no perm, no protocol plumbing, and no
        // `name`: it would only carry the email, which is the `email` scope's to release
        // (auth-track 15's conformance run).
        userinfo
            .EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["sub", "email", "email_verified", "role", "auth_time"], ignoreOrder: true);
        userinfo.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        userinfo.GetProperty("email").GetString().ShouldBe(email);
        userinfo.GetProperty("email_verified").GetBoolean().ShouldBeTrue();
        userinfo.GetProperty("role").GetString().ShouldBe("auditor");

        // The step-10 contract: the id token and userinfo agree, claim for claim.
        var identityClaims = OAuth.DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!);
        foreach (var name in new[] { "sub", "email", "role" })
        {
            identityClaims
                .GetProperty(name)
                .GetString()
                .ShouldBe(userinfo.GetProperty(name).GetString(), $"claim '{name}'");
        }

        identityClaims
            .GetProperty("auth_time")
            .GetInt64()
            .ShouldBe(userinfo.GetProperty("auth_time").GetInt64(), "claim 'auth_time'");

        // A JSON boolean in both places, not the string "true".
        identityClaims
            .GetProperty("email_verified")
            .GetBoolean()
            .ShouldBe(
                userinfo.GetProperty("email_verified").GetBoolean(),
                "claim 'email_verified'"
            );

        identityClaims.TryGetProperty("name", out _).ShouldBeFalse();
        identityClaims.TryGetProperty("perm", out _).ShouldBeFalse();
    }

    // The conformance run's finding: Identity mints `name` from UserName, which is the email —
    // so before auth-track 15 the `profile` scope quietly disclosed what only the `email` scope
    // may release. The email now travels under exactly one scope, and `name` stays absent until
    // an account has something that isn't the email to put in it.
    [Fact]
    public async Task ProfileScopeAlone_DoesNotDiscloseTheEmail()
    {
        var email = $"userinfo-leak-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        var tokens = await TokensAsync(
            "openid profile",
            email,
            TestContext.Current.CancellationToken
        );

        var identityClaims = OAuth.DecodeJwtPayload(tokens.GetProperty("id_token").GetString()!);
        identityClaims.TryGetProperty("name", out _).ShouldBeFalse();
        identityClaims.TryGetProperty("email", out _).ShouldBeFalse();

        var (status, userinfo) = await UserInfoAsync(
            tokens.GetProperty("access_token").GetString()!,
            TestContext.Current.CancellationToken
        );
        status.ShouldBe(HttpStatusCode.OK);
        userinfo.TryGetProperty("name", out _).ShouldBeFalse();
        userinfo.TryGetProperty("email", out _).ShouldBeFalse();
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

        // roles was not granted, so role does not exist here — not even as an empty value —
        // and the same scope gate keeps it out of the access token too.
        userinfo
            .EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["sub", "email", "email_verified", "auth_time"], ignoreOrder: true);
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
