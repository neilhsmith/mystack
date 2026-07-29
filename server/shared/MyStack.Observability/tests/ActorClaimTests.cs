using System.Security.Claims;
using Shouldly;

namespace MyStack.Observability.Tests;

public sealed class ActorClaimTests
{
    [Fact]
    public void Sub_ReturnsTheActor_FromAnRfc8693ActClaim()
    {
        var principal = PrincipalWithAct("""{"sub":"admin-1"}""");

        ActorClaim.Sub(principal).ShouldBe("admin-1");
    }

    [Fact]
    public void Sub_ReturnsTheTopLevelActor_OfADelegationChain()
    {
        var principal = PrincipalWithAct("""{"sub":"admin-1","act":{"sub":"service-2"}}""");

        ActorClaim.Sub(principal).ShouldBe("admin-1");
    }

    [Fact]
    public void Sub_ReturnsNull_WithoutAnActClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        ActorClaim.Sub(principal).ShouldBeNull();
    }

    [Theory]
    [InlineData("admin-1")] // a bare string is not the RFC shape
    [InlineData("""{"iss":"auth"}""")] // no sub member
    [InlineData("""{"sub":42}""")] // sub is not a string
    [InlineData("""{"sub":""}""")] // sub is empty
    [InlineData("""["admin-1"]""")] // not an object
    [InlineData("{not json")]
    public void Sub_ReturnsNull_WhenTheClaimIsNotTheRfcShape(string value)
    {
        var principal = PrincipalWithAct(value);

        ActorClaim.Sub(principal).ShouldBeNull();
    }

    private static ClaimsPrincipal PrincipalWithAct(string value) =>
        new(new ClaimsIdentity([new Claim("act", value)], authenticationType: "test"));
}
