namespace EmbodySense.Core.Common.Secrets.Redaction.Models;

/// <summary>
/// Describes whether a bounded text-redaction operation completed or failed closed.
/// </summary>
public enum RedactionStatus
{
    /// <summary>The complete input was inspected and projected.</summary>
    Completed,

    /// <summary>The sensitive-value scope was invalid, so no input content was projected.</summary>
    ScopeLimitExceeded,

    /// <summary>The input exceeded the configured character limit, so no input content was projected.</summary>
    InputLimitExceeded,

    /// <summary>The projected output would exceed its configured limit, so no input content was projected.</summary>
    OutputLimitExceeded,

    /// <summary>The deterministic comparison budget was exhausted, so no input content was projected.</summary>
    WorkLimitExceeded,

    /// <summary>The first replacement pass synthesized another scoped value, so no input content was projected.</summary>
    ProjectionSafetyFailed
}
