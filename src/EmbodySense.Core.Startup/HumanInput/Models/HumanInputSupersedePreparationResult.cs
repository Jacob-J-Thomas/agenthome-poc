namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one bounded opaque successor-candidate preparation outcome.</summary>
/// <param name="Status">The preparation disposition.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="CandidateKey">The opaque short-lived key when preparation succeeded.</param>
/// <param name="ExpiresAtUtc">The trusted candidate expiration when preparation succeeded.</param>
/// <param name="Error">A bounded value-free failure token.</param>
public sealed record HumanInputSupersedePreparationResult(
    HumanInputSupersedePreparationStatus Status,
    string RequestId,
    string? CandidateKey,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);
