using Microsoft.AspNetCore.Identity;

namespace MyStack.Auth.Data;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
        Id = Guid.CreateVersion7();
    }

    public ApplicationRole(string roleName)
        : this()
    {
        Name = roleName;
    }
}
