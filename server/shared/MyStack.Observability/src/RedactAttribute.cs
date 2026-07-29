namespace MyStack.Observability;

/// <summary>
/// Marks a request field whose value must never reach a log line or a span attribute.
/// </summary>
/// <remarks>
/// The attribute is the contract; the masking machinery arrives with the first body-logging
/// consumer. It lives here so both apps mark fields the same way from the start — retrofitting the
/// marker across existing DTOs is exactly the kind of sweep this library exists to avoid.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RedactAttribute : Attribute;
