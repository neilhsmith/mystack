namespace Api.Features.Auth;

/// <summary>
/// Audience identifiers. The API audience is stamped onto issued tokens (via the
/// <see cref="Scopes"/>' <c>Resources</c> list, set by the OpenIddict scope seeder) and
/// is the value the validation pipeline checks <c>aud</c> against on each request.
/// </summary>
public static class AuthAudiences
{
    /// <summary>
    /// Audience for the API's resource-server endpoints (<c>/v1/*</c>). Tokens whose
    /// <c>aud</c> claim doesn't include this value get a 403 <c>insufficient_access</c>.
    /// </summary>
    public const string ApiAudience = "mystack-api";
}
