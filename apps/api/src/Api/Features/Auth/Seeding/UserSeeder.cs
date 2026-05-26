using Api.Identity;
using Api.Rbac;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Api.Features.Auth.Seeding;

/// <summary>
/// Creates the three expected dev users (<c>globaladmin@</c>, <c>admin@</c>, <c>user@</c>)
/// if they don't already exist, and assigns each to the matching role from
/// <see cref="Roles.Catalog"/>. Passwords come from <see cref="AuthSeedOptions.Users"/>;
/// when blank (the default in <c>appsettings.json</c>) the user is NOT seeded — we don't
/// silently invent passwords in production.
/// <para>
/// On subsequent runs, existing users are left untouched (password rotations, role edits
/// done in an admin UI persist). Missing users get added; missing role assignments get
/// fixed up.
/// </para>
/// </summary>
public sealed class UserSeeder
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly AuthSeedOptions _options;
    private readonly ILogger<UserSeeder> _logger;

    public UserSeeder(
        UserManager<ApplicationUser> users,
        IOptions<AuthSeedOptions> options,
        ILogger<UserSeeder> logger)
    {
        _users = users;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var defs = new (string Email, string Password, string Role, string DisplayName)[]
        {
            (_options.Users.GlobalAdminEmail, _options.Users.GlobalAdminPassword, Roles.GlobalAdmin, "Global Admin"),
            (_options.Users.AdminEmail, _options.Users.AdminPassword, Roles.Admin, "Admin"),
            (_options.Users.UserEmail, _options.Users.UserPassword, Roles.User, "User"),
        };

        foreach (var def in defs)
        {
            if (string.IsNullOrWhiteSpace(def.Password))
            {
                _logger.LogInformation(
                    "Skipping seed user {Email} ({Role}) — no password configured. " +
                    "Set Seed:Users:* in configuration to seed dev users.",
                    def.Email,
                    def.Role);
                continue;
            }

            var existing = await _users.FindByEmailAsync(def.Email);
            if (existing is null)
            {
                _logger.LogInformation("Seeding user {Email} with role {Role}.", def.Email, def.Role);
                var created = new ApplicationUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = def.Email,
                    Email = def.Email,
                    EmailConfirmed = true,
                    DisplayName = def.DisplayName,
                };

                var result = await _users.CreateAsync(created, def.Password);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to seed user {def.Email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
                }

                existing = created;
            }

            // Idempotent role assignment — AddToRoleAsync is a no-op if already in the role.
            if (!await _users.IsInRoleAsync(existing, def.Role))
            {
                var assignResult = await _users.AddToRoleAsync(existing, def.Role);
                if (!assignResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to assign role {def.Role} to {def.Email}: {string.Join("; ", assignResult.Errors.Select(e => e.Description))}");
                }
            }
        }
    }
}
