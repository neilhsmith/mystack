using Shouldly;

namespace MyStack.Email.Tests;

public sealed class EmailShapeTests
{
    [Fact]
    public void A_bare_string_converts_to_an_address()
    {
        EmailAddress address = "person@mystack.test";

        address.Address.ShouldBe("person@mystack.test");
        address.Name.ShouldBeNull();
    }

    [Fact]
    public void Rendered_content_addresses_a_single_recipient_with_both_bodies()
    {
        var content = new EmailContent("Welcome", "<p>Hello.</p>", "Hello.");

        var message = content.ToMessage("person@mystack.test");

        message.To.ShouldHaveSingleItem().Address.ShouldBe("person@mystack.test");
        message.Subject.ShouldBe("Welcome");
        message.HtmlBody.ShouldBe("<p>Hello.</p>");
        message.TextBody.ShouldBe("Hello.");
    }
}
