using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Telemetry;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class RegisterFlowTests(AuthAppFixture app)
{
    private const string GenericPanel = "If that address is valid, we've sent a confirmation email";
    private const string GenericSignInError = "The email or password is incorrect.";

    [Fact]
    public async Task Register_Confirm_SignIn_TheWholePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"register-{Guid.NewGuid():N}@example.test";
        using var client = app.CreateFlowClient();

        var registered = await PageForms.SubmitAsync(
            client,
            "/register",
            NewAccountForm(email),
            cancellationToken
        );
        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await registered.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(GenericPanel);

        // The email rode the outbox to the worker's queue, both bodies carrying the link.
        var sent = await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken);
        sent.Email.Subject.ShouldBe("Confirm your MyStack email");
        sent.Email.HtmlBody.ShouldNotBeNull().ShouldContain("/confirm-email");
        var (linkPath, _, _) = sent.ParseAccountLink();
        linkPath.ShouldStartWith("/confirm-email?");

        // Until the button is pressed the account stays inert: signing in fails, and — the
        // link-scanner property — merely GETting the emailed URL changes nothing either.
        (
            await (
                await OAuth.SignInAsync(
                    client,
                    email,
                    AuthAppFixture.DefaultPassword,
                    cancellationToken: cancellationToken
                )
            ).Content.ReadAsStringAsync(cancellationToken)
        ).ShouldContain(GenericSignInError);

        var confirmPage = await client.GetAsync(linkPath, cancellationToken);
        confirmPage.EnsureSuccessStatusCode();
        var confirmHtml = await confirmPage.Content.ReadAsStringAsync(cancellationToken);
        confirmHtml.ShouldContain("Confirm my email");

        (
            await (
                await OAuth.SignInAsync(
                    client,
                    email,
                    AuthAppFixture.DefaultPassword,
                    cancellationToken: cancellationToken
                )
            ).Content.ReadAsStringAsync(cancellationToken)
        ).ShouldContain(GenericSignInError);

        // The POST is what consumes the token.
        var confirmed = await PageForms.PostAsync(
            client,
            linkPath,
            confirmHtml,
            [],
            cancellationToken
        );
        (await confirmed.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "Email confirmed"
        );

        var signedIn = await OAuth.SignInAsync(
            client,
            email,
            AuthAppFixture.DefaultPassword,
            cancellationToken: cancellationToken
        );
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task Register_ExistingConfirmedEmail_LooksIdenticalAndSendsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var registrations = CreateCollector("auth.registrations");
        var email = $"duplicate-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email);

        using var client = app.CreateFlowClient();
        var response = await PageForms.SubmitAsync(
            client,
            "/register",
            NewAccountForm(email),
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(GenericPanel);

        await WorkerQueue.EnsureNoEmailAsync(app, email, cancellationToken);

        // The response hides the outcome; the operator's view of it lives here.
        registrations
            .GetMeasurementSnapshot()
            .ShouldContain(measurement =>
                (string?)measurement.Tags["outcome"] == "already_registered"
            );
    }

    [Fact]
    public async Task Register_ExistingUnconfirmedEmail_ResendsTheConfirmation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"unconfirmed-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, emailConfirmed: false);

        using var client = app.CreateFlowClient();
        var response = await PageForms.SubmitAsync(
            client,
            "/register",
            NewAccountForm(email),
            cancellationToken
        );

        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(GenericPanel);

        var sent = await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken);
        sent.Email.Subject.ShouldBe("Confirm your MyStack email");
    }

    [Fact]
    public async Task Register_WeakPassword_FailsTheSameForKnownAndUnknownAddresses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var known = $"weak-known-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(known);

        using var client = app.CreateFlowClient();
        var responses = new List<string>();
        foreach (var email in new[] { known, $"weak-unknown-{Guid.NewGuid():N}@example.test" })
        {
            var response = await PageForms.SubmitAsync(
                client,
                "/register",
                NewAccountForm(email, password: "short"),
                cancellationToken
            );
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            responses.Add(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        // The policy error must not depend on whether the address has an account, or a weak
        // password becomes an existence probe.
        foreach (var html in responses)
        {
            html.ShouldContain("at least 12 characters");
            html.ShouldNotContain(GenericPanel);
        }
    }

    [Fact]
    public async Task ConfirmEmail_BadLinks_CollapseToOneGenericFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"badlink-{Guid.NewGuid():N}@example.test";
        var user = await app.CreateUserAsync(email, emailConfirmed: false);

        using var client = app.CreateFlowClient();

        // Undecodable token, decodable-but-garbage token, unknown user id: one answer.
        foreach (
            var url in new[]
            {
                $"/confirm-email?userId={user.Id}&token=not!base64url",
                $"/confirm-email?userId={user.Id}&token=bm90LWEtcmVhbC10b2tlbg",
                $"/confirm-email?userId={Guid.NewGuid()}&token=bm90LWEtcmVhbC10b2tlbg",
                $"/confirm-email?userId=not-a-guid&token=bm90LWEtcmVhbC10b2tlbg",
            }
        )
        {
            var response = await PageForms.SubmitAsync(client, url, [], cancellationToken);
            (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
                "The confirmation link is invalid or has expired."
            );
        }
    }

    [Fact]
    public async Task ConfirmEmail_SecondPress_SaysAlreadyConfirmed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"twice-{Guid.NewGuid():N}@example.test";
        using var client = app.CreateFlowClient();

        await PageForms.SubmitAsync(client, "/register", NewAccountForm(email), cancellationToken);
        var sent = await WorkerQueue.WaitForEmailAsync(app, email, cancellationToken);
        var (linkPath, _, _) = sent.ParseAccountLink();

        (
            await (
                await PageForms.SubmitAsync(client, linkPath, [], cancellationToken)
            ).Content.ReadAsStringAsync(cancellationToken)
        ).ShouldContain("Email confirmed");

        (
            await (
                await PageForms.SubmitAsync(client, linkPath, [], cancellationToken)
            ).Content.ReadAsStringAsync(cancellationToken)
        ).ShouldContain("Already confirmed");
    }

    [Fact]
    public async Task ResendConfirmation_OnlyAnUnconfirmedAccountGetsMail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var confirmations = CreateCollector("auth.email_confirmations");
        var unconfirmed = $"resend-{Guid.NewGuid():N}@example.test";
        var confirmed = $"resend-done-{Guid.NewGuid():N}@example.test";
        var unknown = $"resend-none-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(unconfirmed, emailConfirmed: false);
        await app.CreateUserAsync(confirmed);

        using var client = app.CreateFlowClient();
        var pages = new List<string>();
        foreach (var email in new[] { unconfirmed, confirmed, unknown })
        {
            var response = await PageForms.SubmitAsync(
                client,
                "/resend-confirmation",
                new Dictionary<string, string> { ["Email"] = email },
                cancellationToken
            );
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            pages.Add(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        foreach (var html in pages)
        {
            html.ShouldContain("If that address needs it, we've sent a confirmation email.");
        }

        (
            await WorkerQueue.WaitForEmailAsync(app, unconfirmed, cancellationToken)
        ).Email.Subject.ShouldBe("Confirm your MyStack email");
        await WorkerQueue.EnsureNoEmailAsync(app, confirmed, cancellationToken);
        await WorkerQueue.EnsureNoEmailAsync(app, unknown, cancellationToken);

        var outcomes = confirmations
            .GetMeasurementSnapshot()
            .Select(measurement => (string?)measurement.Tags["outcome"])
            .ToList();
        outcomes.ShouldContain("resent");
        outcomes.ShouldContain("resend_already_confirmed");
        outcomes.ShouldContain("resend_unknown_email");
    }

    // The schema stores 256 characters of email; anything longer must die as a validation
    // message, not as a database truncation error surfacing as a distinguishable 500.
    [Fact]
    public async Task AnOversizedEmail_IsAValidationError_NotA500()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = app.CreateFlowClient();
        var oversized = $"{new string('a', 250)}@example.test";

        var response = await PageForms.SubmitAsync(
            client,
            "/register",
            NewAccountForm(oversized),
            cancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(
            "256 characters or fewer"
        );
    }

    private static Dictionary<string, string> NewAccountForm(
        string email,
        string password = AuthAppFixture.DefaultPassword
    ) =>
        new()
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ConfirmPassword"] = password,
        };

    private MetricCollector<long> CreateCollector(string instrument) =>
        new(app.Services.GetRequiredService<IMeterFactory>(), AuthMetrics.MeterName, instrument);
}
