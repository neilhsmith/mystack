using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Variant of <see cref="ApiTestFactory"/> that drops <c>RateLimiting:PermitLimit</c>
/// to 3 so a rate-limit test can hit the cap in a handful of requests. Boots its own
/// Postgres container (single instance shared via <see cref="IClassFixture{TFixture}"/>
/// on <c>RateLimitingTests</c>) — separate from the main <see cref="ApiTestFactory"/>
/// so other test classes keep the lenient dev defaults and aren't throttled.
/// </summary>
public sealed class RateLimitedTestFactory : ApiTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = "3",
                ["RateLimiting:WindowSeconds"] = "60",
            });
        });
    }
}
