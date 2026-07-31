using System.Net;
using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

// The whole device dance (RFC 8628), driven from both seats: the browserless device polling the
// token endpoint, and the user approving from a signed-in browser on the verification page.
public sealed class DeviceFlowTests(AuthAppFixture app)
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    [Fact]
    public async Task DeviceDance_PendingThenApproveThenTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The device's seat: no cookies, no browser, just its client id.
        var device = app.CreateFlowClient();
        var start = await StartAsync(
            device,
            "openid email profile roles api.read offline_access",
            cancellationToken
        );

        var deviceCode = start.GetProperty("device_code").GetString()!;
        var userCode = start.GetProperty("user_code").GetString()!;
        start.GetProperty("verification_uri").GetString()!.ShouldEndWith("/connect/verify");
        start
            .GetProperty("verification_uri_complete")
            .GetString()!
            .ShouldContain("user_code=", customMessage: "the one-tap link should carry the code");
        start.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);

        // Polling before anyone approved: the RFC's "keep waiting".
        var pending = await OAuth.ExchangeAsync(
            device,
            PollForm(deviceCode),
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        pending.GetProperty("error").GetString().ShouldBe("authorization_pending");

        // The user's seat: sign in on a real browser and follow the verification link.
        var (browser, user) = await SignedInBrowserAsync(cancellationToken);
        var html = await LoadVerificationAsync(browser, userCode, cancellationToken);
        html.ShouldContain(AuthAppFixture.DeviceClientDisplayName);

        var approve = await PageForms.PostAsync(
            browser,
            "/connect/verify?handler=Approve",
            html,
            new Dictionary<string, string> { ["user_code"] = userCode },
            cancellationToken
        );
        approve.StatusCode.ShouldBe(HttpStatusCode.Found);
        approve.Headers.Location!.ToString().ShouldContain("done=approved");

        // The payoff poll: tokens minted for the approving user, through the same principal
        // funnel every user token takes.
        var tokens = await OAuth.ExchangeAsync(
            device,
            PollForm(deviceCode),
            cancellationToken: cancellationToken
        );
        tokens.GetProperty("refresh_token").GetString().ShouldNotBeNull();
        tokens.GetProperty("id_token").GetString().ShouldNotBeNull();

        var payload = OAuth.DecodeJwtPayload(tokens.GetProperty("access_token").GetString()!);
        payload.GetProperty("sub").GetString().ShouldBe(user.Id.ToString());
        payload.GetProperty("role").GetString().ShouldBe("user");
        payload.GetProperty("scope").GetString()!.ShouldContain("api.read");

        // A device code is single-use: redeeming it again is a protocol error, not more tokens.
        var replay = await OAuth.ExchangeAsync(
            device,
            PollForm(deviceCode),
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        replay.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Deny_MakesThePollReturnAccessDenied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var device = app.CreateFlowClient();
        var start = await StartAsync(device, "openid api.read", cancellationToken);
        var deviceCode = start.GetProperty("device_code").GetString()!;
        var userCode = start.GetProperty("user_code").GetString()!;

        var (browser, _) = await SignedInBrowserAsync(cancellationToken);
        var html = await LoadVerificationAsync(browser, userCode, cancellationToken);

        var deny = await PageForms.PostAsync(
            browser,
            "/connect/verify?handler=Deny",
            html,
            new Dictionary<string, string> { ["user_code"] = userCode },
            cancellationToken
        );
        deny.StatusCode.ShouldBe(HttpStatusCode.Found);
        deny.Headers.Location!.ToString().ShouldContain("done=denied");

        var denied = await OAuth.ExchangeAsync(
            device,
            PollForm(deviceCode),
            HttpStatusCode.BadRequest,
            cancellationToken
        );
        denied.GetProperty("error").GetString().ShouldBe("access_denied");
    }

    [Fact]
    public async Task Verification_WithAnUnknownCode_RedisplaysTheEntryForm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var (browser, _) = await SignedInBrowserAsync(cancellationToken);
        var html = await LoadVerificationAsync(browser, "BOGUS-CODE", cancellationToken);

        // "wasn't recognized", minus the apostrophe Razor HTML-encodes.
        html.ShouldContain("recognized");
        html.ShouldNotContain("handler=Approve");
    }

    [Fact]
    public async Task Verification_Anonymous_RedirectsToSignIn()
    {
        var browser = app.CreateFlowClient();

        var response = await browser.GetAsync(
            "/connect/verify?user_code=ABCD1234",
            TestContext.Current.CancellationToken
        );

        // Approval binds the device to whoever approves, so the page demands a signed-in user
        // before rendering anything — and the code survives the round trip in ReturnUrl.
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        new Uri(location).AbsolutePath.ShouldBe("/signin");
        Uri.UnescapeDataString(location).ShouldContain("user_code=ABCD1234");
    }

    [Fact]
    public async Task DeviceEndpoint_RefusesABrowserClient()
    {
        var response = await app.Client.PostAsync(
            "/connect/device",
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = AuthAppFixture.ClientId }
            ),
            TestContext.Current.CancellationToken
        );

        // The seeder grants the device endpoint to device clients alone.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        body.RootElement.GetProperty("error").GetString().ShouldBe("unauthorized_client");
    }

    private static async Task<JsonElement> StartAsync(
        HttpClient device,
        string scope,
        CancellationToken cancellationToken
    )
    {
        var response = await device.PostAsync(
            "/connect/device",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = AuthAppFixture.DeviceClientId,
                    ["scope"] = scope,
                }
            ),
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Dictionary<string, string> PollForm(string deviceCode) =>
        new()
        {
            ["grant_type"] = DeviceGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = AuthAppFixture.DeviceClientId,
        };

    private async Task<(HttpClient Browser, Data.ApplicationUser User)> SignedInBrowserAsync(
        CancellationToken cancellationToken
    )
    {
        var email = $"device-{Guid.NewGuid():N}@mystack.test";
        var user = await app.CreateUserAsync(email, role: "user");

        var browser = app.CreateFlowClient();
        var signIn = await OAuth.SignInAsync(
            browser,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);

        return (browser, user);
    }

    private static async Task<string> LoadVerificationAsync(
        HttpClient browser,
        string userCode,
        CancellationToken cancellationToken
    )
    {
        var page = await browser.GetAsync(
            $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}",
            cancellationToken
        );
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await page.Content.ReadAsStringAsync(cancellationToken);
    }
}
