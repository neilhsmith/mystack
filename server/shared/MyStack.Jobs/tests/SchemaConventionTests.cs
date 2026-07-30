using Shouldly;

namespace MyStack.Jobs.Tests;

public sealed class SchemaConventionTests
{
    // The per-app schema is the isolation boundary architecture §3.3 leans on — hangfire_auth
    // and hangfire_api must never collapse into one shared queue.
    [Fact]
    public void Each_app_gets_its_own_schema()
    {
        JobsExtensions.SchemaFor("auth").ShouldBe("hangfire_auth");
        JobsExtensions.SchemaFor("api").ShouldBe("hangfire_api");
    }
}
