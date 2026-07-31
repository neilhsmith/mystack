using MimeKit;
using Shouldly;

namespace MyStack.Email.Tests;

// The EmailMessage → MimeMessage mapping, provable without an SMTP server on the other end.
public sealed class MimeMappingTests
{
    private static readonly EmailOptions Options = new()
    {
        Host = "localhost",
        From = "no-reply@mystack.test",
        FromName = "MyStack",
    };

    [Fact]
    public void Both_bodies_become_a_multipart_alternative()
    {
        var mime = SmtpEmailSender.ToMimeMessage(
            new EmailMessage
            {
                To = [new EmailAddress("person@mystack.test", "Person")],
                Subject = "Welcome",
                HtmlBody = "<p>Hello.</p>",
                TextBody = "Hello.",
            },
            Options
        );

        mime.Subject.ShouldBe("Welcome");
        mime.To.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("person@mystack.test");
        mime.HtmlBody.ShouldBe("<p>Hello.</p>");
        mime.TextBody.ShouldBe("Hello.");
    }

    [Fact]
    public void The_configured_sender_is_the_default_from()
    {
        var mime = SmtpEmailSender.ToMimeMessage(Minimal(), Options);

        var from = mime.From.Mailboxes.ShouldHaveSingleItem();
        from.Address.ShouldBe("no-reply@mystack.test");
        from.Name.ShouldBe("MyStack");
    }

    [Fact]
    public void A_message_from_overrides_the_configured_sender()
    {
        var mime = SmtpEmailSender.ToMimeMessage(
            Minimal() with
            {
                From = new EmailAddress("security@mystack.test", "MyStack Security"),
            },
            Options
        );

        mime.From.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("security@mystack.test");
    }

    [Fact]
    public void Copies_and_reply_to_are_carried()
    {
        var mime = SmtpEmailSender.ToMimeMessage(
            Minimal() with
            {
                Cc = [new EmailAddress("cc@mystack.test")],
                Bcc = [new EmailAddress("bcc@mystack.test")],
                ReplyTo = new EmailAddress("support@mystack.test"),
            },
            Options
        );

        mime.Cc.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("cc@mystack.test");
        mime.Bcc.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("bcc@mystack.test");
        mime.ReplyTo.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("support@mystack.test");
    }

    [Fact]
    public void An_attachment_keeps_its_name_type_and_content()
    {
        var content = "hello,attachment"u8.ToArray();

        var mime = SmtpEmailSender.ToMimeMessage(
            Minimal() with
            {
                Attachments = [new EmailAttachment("export.csv", "text/csv", content)],
            },
            Options
        );

        // Assignable, not exact: MimeKit specializes text/* attachments into TextPart.
        var attachment = mime.Attachments.ShouldHaveSingleItem().ShouldBeAssignableTo<MimePart>();
        attachment.FileName.ShouldBe("export.csv");
        attachment.ContentType.MimeType.ShouldBe("text/csv");

        using var decoded = new MemoryStream();
        attachment
            .Content.ShouldNotBeNull()
            .DecodeTo(decoded, TestContext.Current.CancellationToken);
        decoded.ToArray().ShouldBe(content);
    }

    [Fact]
    public void A_message_without_recipients_is_a_bug()
    {
        Should
            .Throw<InvalidOperationException>(() =>
                SmtpEmailSender.ToMimeMessage(Minimal() with { To = [] }, Options)
            )
            .Message.ShouldContain("recipient");
    }

    [Fact]
    public void A_message_without_a_body_is_a_bug()
    {
        Should
            .Throw<InvalidOperationException>(() =>
                SmtpEmailSender.ToMimeMessage(
                    Minimal() with
                    {
                        HtmlBody = null,
                        TextBody = null,
                    },
                    Options
                )
            )
            .Message.ShouldContain("body");
    }

    private static EmailMessage Minimal() =>
        new()
        {
            To = [new EmailAddress("person@mystack.test")],
            Subject = "Welcome",
            TextBody = "Hello.",
        };
}
