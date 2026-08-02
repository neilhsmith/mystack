namespace MyStack.Auth.Oidc;

/// <summary>
/// RFC 7591's <c>client_uri</c> — the client's home page — kept in OpenIddict's per-client
/// settings bag because the application store has no first-class column for it. Rendered pages
/// read it to offer a way back to the app instead of guessing at auth's own root: a link on an
/// OP page is either part of the flow the user is in, or a return the client's registration
/// vouches for — never a guess.
/// </summary>
internal static class ClientMetadata
{
    public const string ClientUriSetting = "mystack:client_uri";
}
