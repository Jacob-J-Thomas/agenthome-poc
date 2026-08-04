namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Represents one ordered structured property with a sanitized key and value.
/// </summary>
/// <param name="Key">The sanitized property key.</param>
/// <param name="Value">The bounded projected value.</param>
public sealed record RedactedDataProperty(string Key, RedactedDataNode Value);
