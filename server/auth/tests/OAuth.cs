using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

// The client half of the protocol, shared by every flow test.
internal static class OAuth
{
    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        // Hex keeps every character inside RFC 7636's unreserved set.
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return (verifier, challenge);
    }

    public static string AuthorizeUrl(
        string challenge,
        string scope,
        string clientId = AuthAppFixture.ClientId
    ) =>
        $"/connect/authorize?client_id={clientId}"
        + $"&redirect_uri={Uri.EscapeDataString(AuthAppFixture.RedirectUri)}"
        + $"&response_type=code&scope={Uri.EscapeDataString(scope)}"
        + $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

    public static Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string email,
        string password,
        string? returnUrl = null,
        CancellationToken cancellationToken = default
    ) =>
        PageForms.SubmitAsync(
            client,
            returnUrl is null ? "/signin" : $"/signin?ReturnUrl={Uri.EscapeDataString(returnUrl)}",
            new Dictionary<string, string> { ["Email"] = email, ["Password"] = password },
            cancellationToken
        );

    /// <summary>
    /// Drives /connect/authorize with an already signed-in client and returns the code captured
    /// from the redirect back to the client's callback.
    /// </summary>
    public static async Task<string> AuthorizeAsync(
        HttpClient client,
        string challenge,
        string scope,
        CancellationToken cancellationToken = default
    )
    {
        var response = await client.GetAsync(AuthorizeUrl(challenge, scope), cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(AuthAppFixture.RedirectUri);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["error"].ShouldBeNull(query["error_description"]);

        return query["code"].ShouldNotBeNull();
    }

    public static async Task<JsonElement> ExchangeAsync(
        HttpClient client,
        Dictionary<string, string> form,
        HttpStatusCode expected = HttpStatusCode.OK,
        CancellationToken cancellationToken = default
    )
    {
        var response = await client.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(form),
            cancellationToken
        );

        response.StatusCode.ShouldBe(expected);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
