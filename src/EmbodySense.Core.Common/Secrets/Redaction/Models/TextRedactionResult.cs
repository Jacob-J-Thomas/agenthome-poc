namespace EmbodySense.Core.Common.Secrets.Redaction.Models;

/// <summary>
/// Contains a bounded text projection and value-free evidence describing its redaction.
/// </summary>
/// <param name="Value">The sanitized text, or a fail-closed marker when the operation reached a limit.</param>
/// <param name="Summary">Value-free operation evidence.</param>
public sealed record TextRedactionResult(string Value, RedactionSummary Summary);
