using System.Diagnostics.Metrics;

namespace MyStack.Auth.Telemetry;

// The auth counters from architecture §3's metric table. Tag values come from closed sets only —
// never client input — so a probe can't mint time series. The account counters carry the
// anti-enumeration flows' honest outcomes: the `unknown_email` a generic 200 deliberately hides
// from the response is a tag value here, which is what makes an enumeration run visible to an
// operator without the response giving anything away.
public sealed class AuthMetrics
{
    public const string MeterName = "MyStack.Auth";

    private readonly Counter<long> signIns;
    private readonly Counter<long> grants;
    private readonly Counter<long> registrations;
    private readonly Counter<long> emailConfirmations;
    private readonly Counter<long> passwordResets;
    private readonly Counter<long> passwordChanges;
    private readonly Counter<long> logoutNotifications;

    public AuthMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        signIns = meter.CreateCounter<long>(
            "auth.sign_ins",
            description: "Password sign-in attempts at the sign-in page, by result."
        );
        grants = meter.CreateCounter<long>(
            "auth.oauth.grants",
            description: "Token endpoint responses, by grant type and result."
        );
        registrations = meter.CreateCounter<long>(
            "auth.registrations",
            description: "Registration attempts, by honest outcome."
        );
        emailConfirmations = meter.CreateCounter<long>(
            "auth.email_confirmations",
            description: "Email confirmation and resend attempts, by honest outcome."
        );
        passwordResets = meter.CreateCounter<long>(
            "auth.password_resets",
            description: "Password reset requests and completions, by stage and honest outcome."
        );
        passwordChanges = meter.CreateCounter<long>(
            "auth.password_changes",
            description: "Signed-in password changes, by outcome."
        );
        logoutNotifications = meter.CreateCounter<long>(
            "auth.logout_notifications",
            description: "Back-channel logout deliveries, by client and outcome."
        );
    }

    public void SignIn(string result) =>
        signIns.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Grant(string grantType, string result) =>
        grants.Add(
            1,
            new KeyValuePair<string, object?>("grant_type", grantType),
            new KeyValuePair<string, object?>("result", result)
        );

    public void Registration(string outcome) =>
        registrations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void EmailConfirmation(string outcome) =>
        emailConfirmations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void PasswordReset(string stage, string outcome) =>
        passwordResets.Add(
            1,
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("outcome", outcome)
        );

    public void PasswordChange(string outcome) =>
        passwordChanges.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    // client_id is operator-declared seed config, never a caller-supplied string — the closed-set
    // rule holds because only registered clients are ever notified.
    public void LogoutNotification(string clientId, string outcome) =>
        logoutNotifications.Add(
            1,
            new KeyValuePair<string, object?>("client_id", clientId),
            new KeyValuePair<string, object?>("outcome", outcome)
        );
}
