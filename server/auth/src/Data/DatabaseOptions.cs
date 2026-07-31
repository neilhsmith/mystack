namespace MyStack.Auth.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Off unless asked for: an instance that migrates on boot rewrites the schema during a rollback
    // too. Development turns it on; a deployment applies migrations as its own step.
    public bool Migrate { get; init; }

    // On by default everywhere: a fresh database must reach a working state — roles, scopes,
    // clients, someone able to sign in — without hand-work. Safe to leave on because every write
    // is create-only or a real-drift reconcile, and every account is config-declared, so no
    // environment receives anything it didn't declare (architecture §3.4). Off is the escape
    // hatch for an organisation managing clients out of band.
    public bool Seed { get; init; } = true;
}
