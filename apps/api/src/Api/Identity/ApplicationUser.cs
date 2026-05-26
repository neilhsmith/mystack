using Microsoft.AspNetCore.Identity;

namespace Api.Identity;

/// <summary>
/// The user entity stored by ASP.NET Identity. Keys are <see cref="Guid"/>-backed so user
/// ids are sortable v7 GUIDs (matches the convention used by business entities like
/// <see cref="Api.Features.Posts.Post"/>) and so the <c>sub</c> claim in issued tokens
/// can round-trip cleanly through Postgres.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Human-friendly name surfaced in the <c>name</c> claim. Optional — falls back to
    /// <see cref="IdentityUser{TKey}.UserName"/> when not set.
    /// </summary>
    public string? DisplayName { get; set; }
}
