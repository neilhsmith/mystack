using Shouldly;

namespace MyStack.Messaging.Tests;

public sealed class MessagingConventionTests
{
    // The per-app schema is the isolation boundary architecture §3.3 leans on — wolverine_auth
    // and wolverine_worker must never collapse into shared envelope storage.
    [Fact]
    public void Each_app_gets_its_own_envelope_schema()
    {
        MessagingExtensions.SchemaFor("auth").ShouldBe("wolverine_auth");
        MessagingExtensions.SchemaFor("worker").ShouldBe("wolverine_worker");
    }
}
