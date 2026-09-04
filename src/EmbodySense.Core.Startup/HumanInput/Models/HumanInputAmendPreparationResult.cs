namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one opaque amend candidate or a value-free fail-closed disposition.</summary>
/// <param name="Status">The preparation disposition.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="CandidateKey">The short-lived opaque key when preparation succeeds.</param>
/// <param name="ExpiresAtUtc">The trusted candidate-registration expiry when preparation succeeds.</param>
/// <param name="Error">A bounded value-free failure token.</param>
public sealed record HumanInputAmendPreparationResult(
    HumanInputSupersedePreparationStatus Status,
    string RequestId,
    string? CandidateKey,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);
