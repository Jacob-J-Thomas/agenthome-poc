namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Represents a bounded structured value whose textual content has passed through a sensitive-value scope.
/// </summary>
public sealed class RedactedDataNode
{
    internal RedactedDataNode(RedactedDataKind kind, string? text, bool? boolean, IReadOnlyList<RedactedDataProperty> properties, IReadOnlyList<RedactedDataNode> items)
    {
        Kind = kind;
        Text = text;
        Boolean = boolean;
        Properties = properties;
        Items = items;
    }

    /// <summary>Gets the projected value kind.</summary>
    public RedactedDataKind Kind { get; }

    /// <summary>Gets sanitized text for text and marker nodes; otherwise <see langword="null"/>.</summary>
    public string? Text { get; }

    /// <summary>Gets a Boolean scalar value; otherwise <see langword="null"/>.</summary>
    public bool? Boolean { get; }

    /// <summary>Gets ordered, sanitized properties for an object node.</summary>
    public IReadOnlyList<RedactedDataProperty> Properties { get; }

    /// <summary>Gets ordered items for an array node.</summary>
    public IReadOnlyList<RedactedDataNode> Items { get; }
}
