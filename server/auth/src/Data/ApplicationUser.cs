using Microsoft.AspNetCore.Identity;

namespace MyStack.Auth.Data;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        // Version 7 UUIDs sort by creation time, so the primary key index appends instead of
        // fragmenting. Generated here rather than by the database so the id — which becomes the
        // token's `sub` — is known before SaveChanges.
        Id = Guid.CreateVersion7();
    }
}
