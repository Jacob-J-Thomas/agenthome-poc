namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Represents one ordered header with a sanitized name and bounded sanitized values.
/// </summary>
/// <param name="Name">The sanitized header name.</param>
/// <param name="Values">The ordered sanitized header values.</param>
public sealed record RedactedHeader(string Name, IReadOnlyList<string> Values);
