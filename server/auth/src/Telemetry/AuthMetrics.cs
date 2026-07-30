using System.Diagnostics.Metrics;

namespace MyStack.Auth.Telemetry;

// The two OpenIddict-step counters from architecture §3's metric table. Tag values come from
// closed sets only — never client input — so a probe can't mint time series.
public sealed class AuthMetrics
{
    public const string MeterName = "MyStack.Auth";

    private readonly Counter<long> signIns;
    private readonly Counter<long> grants;

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
    }

    public void SignIn(string result) =>
        signIns.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Grant(string grantType, string result) =>
        grants.Add(
            1,
            new KeyValuePair<string, object?>("grant_type", grantType),
            new KeyValuePair<string, object?>("result", result)
        );
}
