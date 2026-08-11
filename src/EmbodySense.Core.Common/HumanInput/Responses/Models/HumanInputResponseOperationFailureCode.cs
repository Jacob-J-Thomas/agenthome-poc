namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one bounded value-free authenticated-response operation failure.</summary>
public enum HumanInputResponseOperationFailureCode
{
    /// <summary>No supported failure was supplied.</summary>
    Unknown = 0,
    /// <summary>No failure applies to a committed operation.</summary>
    None = 1,
    /// <summary>The workspace-global operation identifier was already bound to changed canonical intent.</summary>
    OperationIntentConflict = 2,
    /// <summary>The expected request lifecycle head was stale.</summary>
    OptimisticStateConflict = 3,
    /// <summary>The exact request lifecycle did not exist.</summary>
    RequestNotFound = 4,
    /// <summary>The exact request lifecycle was no longer pending.</summary>
    RequestTerminal = 5,
    /// <summary>The exact targeted response did not exist.</summary>
    ResponseNotFound = 6,
    /// <summary>The exact response was already withdrawn.</summary>
    ResponseAlreadyWithdrawn = 7,
    /// <summary>A new operation duplicated an already retained response identity or active actor response.</summary>
    DuplicateResponse = 8,
    /// <summary>The response targeted a stale request version, hash, binding, or lifecycle expectation.</summary>
    StaleResponse = 9,
    /// <summary>Trusted time was strictly after the inclusive response endpoint.</summary>
    LateResponse = 10,
    /// <summary>The validly correlated response value or explanation was malformed.</summary>
    MalformedResponse = 11,
    /// <summary>The authenticated actor and role were not eligible for the exact request.</summary>
    IneligibleRespondent = 12,
    /// <summary>The authenticated actor role was not eligible to select under the exact manual policy.</summary>
    IneligibleSelector = 13,
    /// <summary>The selected response set was stale, withdrawn, duplicated, cross-bound, or otherwise failed exact policy validation.</summary>
    SelectionConflict = 14,
    /// <summary>The finite retained response bound was exhausted.</summary>
    ResponseLimitExceeded = 15,
    /// <summary>The finite append-only response-operation evidence bound was exhausted.</summary>
    OperationEvidenceLimitExceeded = 16,
    /// <summary>The interoperable optimistic lifecycle-version bound was exhausted.</summary>
    LifecycleVersionLimitExceeded = 17
}
