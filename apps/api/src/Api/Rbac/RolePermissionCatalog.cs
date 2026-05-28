using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Api.Rbac;

/// <summary>
/// Process-wide cache of "what permissions does role X grant?". Read on every authorized
/// request via the permission policy handler, written only by
/// <see cref="RolePermissionCatalogRefresher"/> when it loads the table from Postgres at
/// startup and on its refresh tick.
/// <para>
/// Backed by <see cref="FrozenDictionary{TKey,TValue}"/> + <see cref="FrozenSet{T}"/> for
/// the hot path — both are read-optimised and built once per refresh. Whole-snapshot
/// replacement (not mutation) means readers never see a half-applied update, with no lock
/// in the request path: a single <see cref="Interlocked.Exchange{T}"/> swaps in the new
/// snapshot atomically.
/// </para>
/// <para>
/// Lookups fail closed: an unknown role returns an empty permission set, so a typo in a
/// role-name or a token issued before a refresh can't accidentally grant access.
/// </para>
/// </summary>
public sealed class RolePermissionCatalog
{
    /// <summary>
    /// Always non-null after construction; starts empty so policy checks deny everything
    /// until <see cref="RolePermissionCatalogRefresher"/> has its first successful load.
    /// </summary>
    private Snapshot _current = Snapshot.Empty;

    /// <summary>
    /// Get the permissions granted to <paramref name="role"/> (case-insensitive match on
    /// the role name as stored). Returns an empty set if the role is unknown — see class
    /// remarks for the fail-closed rationale.
    /// </summary>
    public FrozenSet<string> GetPermissions(string role)
    {
        // Volatile read is unnecessary on x86/x64 (atomic reference writes) but we ensure
        // ordering across the Interlocked.Exchange in Replace; the JIT honours that here.
        var snapshot = Volatile.Read(ref _current);
        return snapshot.ByRole.TryGetValue(role, out var permissions)
            ? permissions
            : FrozenSet<string>.Empty;
    }

    /// <summary>
    /// True if any of the supplied <paramref name="roles"/> grants <paramref name="permission"/>.
    /// </summary>
    public bool HasPermission(IEnumerable<string> roles, string permission)
    {
        var snapshot = Volatile.Read(ref _current);
        foreach (var role in roles)
        {
            if (snapshot.ByRole.TryGetValue(role, out var permissions)
                && permissions.Contains(permission))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True once <see cref="RolePermissionCatalogRefresher"/> has populated the catalog
    /// at least once. Used to differentiate "denied because the user really doesn't have
    /// this permission" from "denied because we haven't loaded the catalog yet" in 503
    /// responses / health checks.
    /// </summary>
    public bool IsLoaded => Volatile.Read(ref _current).Loaded;

    /// <summary>
    /// Atomically replace the catalog. Callers build the role→permissions map (loaded
    /// from <c>role_permissions</c> joined with <c>aspnet_roles</c>) and hand it in; the
    /// catalog owns turning it into a frozen snapshot.
    /// </summary>
    internal void Replace(IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesToPermissions)
    {
        var byRole = rolesToPermissions
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .ToFrozenDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToFrozenSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        Interlocked.Exchange(ref _current, new Snapshot(byRole, Loaded: true));
    }

    /// <summary>
    /// Immutable snapshot — passed by reference, never mutated. New refresh produces a
    /// new instance, swapped in via <see cref="Interlocked.Exchange{T}"/>.
    /// </summary>
    private sealed record Snapshot(
        FrozenDictionary<string, FrozenSet<string>> ByRole,
        bool Loaded)
    {
        public static Snapshot Empty { get; } = new(
            FrozenDictionary<string, FrozenSet<string>>.Empty,
            Loaded: false);
    }

    /// <summary>
    /// Read-only view of the current snapshot, useful for diagnostics / admin endpoints.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> GetCurrentSnapshot()
    {
        var snapshot = Volatile.Read(ref _current);
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>();
        foreach (var (role, permissions) in snapshot.ByRole)
        {
            builder[role] = permissions.ToImmutableArray();
        }
        return builder.ToImmutable();
    }
}
