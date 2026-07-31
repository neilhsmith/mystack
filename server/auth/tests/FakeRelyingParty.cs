using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyStack.Auth.Tests;

// A relying party's back-channel logout endpoint, listening on a real loopback port: the host
// under test delivers logout tokens over genuine outbound HTTP, which TestServer's in-memory
// transport could never receive.
public sealed class FakeRelyingParty : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<ReceivedLogout> received = [];

    private FakeRelyingParty(WebApplication app) => this.app = app;

    public string LogoutUri => $"{app.Urls.Single()}/backchannel-logout";

    public IReadOnlyList<ReceivedLogout> Received
    {
        get
        {
            lock (received)
            {
                return [.. received];
            }
        }
    }

    public void Clear()
    {
        lock (received)
        {
            received.Clear();
        }
    }

    public static async Task<FakeRelyingParty> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var relyingParty = new FakeRelyingParty(app);

        app.MapPost(
            "/backchannel-logout",
            async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                relyingParty.Record(new(request.ContentType, form["logout_token"].ToString()));
                return Results.Ok();
            }
        );

        await app.StartAsync();
        return relyingParty;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private void Record(ReceivedLogout logout)
    {
        lock (received)
        {
            received.Add(logout);
        }
    }
}

public sealed record ReceivedLogout(string? ContentType, string LogoutToken);
