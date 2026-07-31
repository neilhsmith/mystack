using OpenIddict.Abstractions;

namespace MyStack.Auth.Oidc;

// A credential change ends every session it can reach: revoking by subject kills the refresh
// tokens and grants on every other device, while already-issued access tokens are stateless JWTs
// and simply age out (OidcOptions' fifteen minutes). Tokens before authorizations, so a failure
// between the two leaves nothing usable behind.
// Public because the public page models take it by constructor (CS0051).
public sealed class TokenRevocationService(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations
)
{
    public async Task RevokeAllAsync(string subject, CancellationToken cancellationToken)
    {
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
    }
}
