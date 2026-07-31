using MyStack.Auth.Telemetry;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

// Counted at ApplyTokenResponse rather than in the endpoint handler: this event sees every token
// response, including the protocol rejections — unsupported grants, bad codes, failed PKCE — that
// never reach passthrough. A counter in the handler would miss exactly the failures worth
// alerting on.
internal sealed class GrantMetricsHandler(AuthMetrics metrics)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        // grant_type is client input; collapsing it to a closed set keeps a probe from minting
        // arbitrary time series. Error codes are OpenIddict's own and already closed.
        var grantType = context.Request?.GrantType switch
        {
            GrantTypes.AuthorizationCode => GrantTypes.AuthorizationCode,
            GrantTypes.RefreshToken => GrantTypes.RefreshToken,
            GrantTypes.ClientCredentials => GrantTypes.ClientCredentials,
            GrantTypes.DeviceCode => GrantTypes.DeviceCode,
            null or "" => "none",
            _ => "unsupported",
        };

        metrics.Grant(grantType, context.Response?.Error ?? "issued");

        return default;
    }
}
