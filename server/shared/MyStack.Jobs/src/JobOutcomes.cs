namespace MyStack.Jobs;

/// <summary>
/// The closed set behind the <c>outcome</c> tag — tag values come from code, never from data.
/// </summary>
internal static class JobOutcomes
{
    public const string Succeeded = "succeeded";

    /// <summary>The execution threw and a retry is scheduled.</summary>
    public const string Failed = "failed";

    /// <summary>Retries are exhausted; the job is parked on the dashboard's Failed page.</summary>
    public const string DeadLettered = "dead_lettered";
}
