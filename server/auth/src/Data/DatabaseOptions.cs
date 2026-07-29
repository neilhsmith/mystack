namespace MyStack.Auth.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Off unless asked for: an instance that migrates on boot rewrites the schema during a rollback
    // too. Development turns it on; a deployment applies migrations as its own step.
    public bool Migrate { get; init; }
}
