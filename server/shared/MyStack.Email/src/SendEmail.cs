namespace MyStack.Email;

/// <summary>
/// The cross-app command (architecture §3.3): any app publishes it to the worker's queue, and the
/// worker delivers it over SMTP. Living in the shared library is what lets publisher and consumer
/// agree on the message without either loading the other's types.
/// </summary>
public sealed record SendEmail(EmailMessage Email);
