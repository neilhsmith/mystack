namespace Api.Features.Auth.Seeding;

/// <summary>
/// Configuration shape for development seed data. Bound from the <c>Seed</c> section of
/// <c>appsettings.{env}.json</c>. Passwords default to empty in <c>appsettings.json</c>
/// (production assumption: real values come from secrets / env vars); the dev override
/// in <c>appsettings.Development.json</c> sets a known throwaway password so the
/// boilerplate works out of the box.
/// </summary>
public sealed class AuthSeedOptions
{
    public const string SectionName = "Seed";

    public SeedUsersOptions Users { get; set; } = new();
    public SeedClientsOptions Clients { get; set; } = new();

    public sealed class SeedUsersOptions
    {
        public string GlobalAdminEmail { get; set; } = "globaladmin@mystack.test";
        public string GlobalAdminPassword { get; set; } = string.Empty;
        public string AdminEmail { get; set; } = "admin@mystack.test";
        public string AdminPassword { get; set; } = string.Empty;
        public string UserEmail { get; set; } = "user@mystack.test";
        public string UserPassword { get; set; } = string.Empty;
    }

    public sealed class SeedClientsOptions
    {
        public string WebClientId { get; set; } = "mystack-web";
        public string WebRedirectUri { get; set; } = "http://localhost:3000/auth/callback";
        public string WebPostLogoutRedirectUri { get; set; } = "http://localhost:3000/";
        public string ServiceClientId { get; set; } = "mystack-service";
        public string ServiceClientSecret { get; set; } = string.Empty;
    }
}
