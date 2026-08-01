using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace MyStack.Auth.Tests;

// TestServer's in-memory connections carry no client address, which would fold every test into
// one rate-limit partition. Registered through DI by the test factory, this wraps the app's
// pipeline and maps a test-supplied header onto the connection so each test client is its own
// caller — the app itself stays free of test awareness.
internal sealed class FakeClientIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                (context, nextMiddleware) =>
                {
                    if (IPAddress.TryParse(context.Request.Headers[HeaderName], out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    return nextMiddleware(context);
                }
            );

            next(app);
        };
}
