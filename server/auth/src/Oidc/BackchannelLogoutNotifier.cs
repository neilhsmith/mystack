using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MyStack.Auth.Telemetry;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

// OIDC Back-Channel Logout 1.0: when a user's session ends, every registered client with a
// logout URI is POSTed a signed logout token, server to server — the notification layer
// OpenIddict's end-session protocol deliberately leaves to the host. The token carries `sub`
// and no `sid`: auth's session store is the Identity cookie, there is no session id to
// correlate, and "sign this user out everywhere" is exactly the semantics single sign-out
// wants. Delivery is concurrent and best-effort — a logout token outlives its usefulness in
// minutes, so queued retries would mostly deliver dead tokens; a missed notification is
// bounded by the consumer's own token lifetime, and the failure is logged and counted.
internal sealed partial class BackchannelLogoutNotifier(
    IOpenIddictApplicationManager applications,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIddictServerOptions> serverOptions,
    AuthMetrics metrics,
    ILogger<BackchannelLogoutNotifier> logger
)
{
    // The application setting the seeder writes a browser client's logout URI into —
    // OpenIddict has no first-class notion of back-channel logout, so this is ours.
    public const string SettingName = "mystack:backchannel_logout_uri";

    public const string HttpClientName = "backchannel-logout";

    private const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    // Bounds clock skew between auth and a consumer, nothing else — the token is redeemed
    // within one HTTP round trip of being minted.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static readonly JsonWebTokenHandler TokenHandler = new()
    {
        SetDefaultTimesOnTokenCreation = false,
    };

    /// <summary>
    /// Delivers a logout token for <paramref name="subject"/> to every registered client that
    /// declares a back-channel logout URI. Deliberately not cancellable: the browser that
    /// initiated the sign-out disconnecting must not stop the other apps from hearing about it.
    /// </summary>
    public async Task NotifyAsync(string subject, Uri requestBase)
    {
        var targets = new List<(string ClientId, string Uri)>();
        await foreach (var application in applications.ListAsync())
        {
            var settings = await applications.GetSettingsAsync(application);
            if (settings.TryGetValue(SettingName, out var uri) && !string.IsNullOrEmpty(uri))
            {
                var clientId = await applications.GetClientIdAsync(application);
                targets.Add((clientId!, uri));
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        // The same issuer the discovery document advertises, so consumers validate `iss`
        // against the metadata they already hold.
        var issuer = (serverOptions.CurrentValue.Issuer ?? requestBase).AbsoluteUri;

        await Task.WhenAll(
            targets.Select(target => DeliverAsync(target.ClientId, target.Uri, subject, issuer))
        );
    }

    private async Task DeliverAsync(string clientId, string uri, string subject, string issuer)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsync(
                uri,
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["logout_token"] = CreateLogoutToken(clientId, subject, issuer),
                    }
                )
            );

            if (response.IsSuccessStatusCode)
            {
                metrics.LogoutNotification(clientId, "delivered");
            }
            else
            {
                metrics.LogoutNotification(clientId, "failed");
                LogDeliveryRejected(logger, clientId, (int)response.StatusCode);
            }
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException)
        {
            metrics.LogoutNotification(clientId, "failed");
            LogDeliveryFailed(logger, clientId, exception);
        }
    }

    // The logout token the spec prescribes: `iss`/`aud`/`iat`/`exp`/`jti`, the subject, the
    // back-channel-logout event claim, a `logout+jwt` type header — and never a `nonce`, which
    // is what stops it doubling as an id token. Signed with the same credentials as every
    // token, so consumers validate it against the published JWKS.
    private string CreateLogoutToken(string clientId, string subject, string issuer)
    {
        var now = DateTimeOffset.UtcNow;

        return TokenHandler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = clientId,
                IssuedAt = now.UtcDateTime,
                Expires = now.Add(Lifetime).UtcDateTime,
                TokenType = "logout+jwt",
                SigningCredentials = serverOptions.CurrentValue.SigningCredentials.First(),
                Claims = new Dictionary<string, object>
                {
                    [Claims.Subject] = subject,
                    [Claims.JwtId] = Guid.NewGuid().ToString(),
                    ["events"] = new Dictionary<string, object>
                    {
                        [LogoutEvent] = new Dictionary<string, object>(),
                    },
                },
            }
        );
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Back-channel logout delivery to client {ClientId} was rejected with status {StatusCode}."
    )]
    private static partial void LogDeliveryRejected(
        ILogger logger,
        string clientId,
        int statusCode
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Back-channel logout delivery to client {ClientId} failed."
    )]
    private static partial void LogDeliveryFailed(
        ILogger logger,
        string clientId,
        Exception exception
    );
}
