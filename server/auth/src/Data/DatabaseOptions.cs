namespace MyStack.Auth.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Off unless asked for: an instance that migrates on boot rewrites the schema during a rollback
    // too. Development turns it on; a deployment applies migrations as its own step.
    public bool Migrate { get; init; }

    public SeedSwitches Seed { get; init; } = new();

    // Two switches, not one (architecture §3.4): production needs the reference half and must
    // never see the sample half, which a single boolean cannot express.
    public sealed class SeedSwitches
    {
        // On by default everywhere: roles, scopes, clients and the bootstrap admin are what the
        // app cannot function without. Off is the escape hatch for an organisation managing
        // clients out of band.
        public bool Reference { get; init; } = true;

        // Demo accounts. Development and e2e turn it on; enabling it in a production environment
        // fails startup rather than obeying.
        public bool Sample { get; init; }
    }
}
