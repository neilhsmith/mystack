namespace MyStack.Auth.Security;

// Cookie names carry the instance name because browsers scope cookies to the host and ignore
// the port: side-by-side stacks on localhost otherwise share one jar, and since each instance
// keeps its own data-protection ring, whichever signed in last would evict the other's session.
internal static class AuthCookies
{
    public static string Application(string instanceName) => $".MyStack.Auth.{instanceName}";

    public static string Antiforgery(string instanceName) => $".MyStack.Antiforgery.{instanceName}";
}
