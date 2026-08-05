namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Identifies the safe projected shape of one structured value.
/// </summary>
public enum RedactedDataKind
{
    /// <summary>A null value.</summary>
    Null,

    /// <summary>A sanitized textual scalar.</summary>
    Text,

    /// <summary>A Boolean scalar.</summary>
    Boolean,

    /// <summary>An ordered object projection.</summary>
    Object,

    /// <summary>An ordered array projection.</summary>
    Array,

    /// <summary>A deterministic bound, cycle, or unsupported-value marker.</summary>
    Marker
}
