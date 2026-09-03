namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns bounded opaque reroute alternatives or a value-free fail-closed disposition.</summary>
/// <param name="Status">The preparation disposition.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="Options">The independently bounded opaque options when preparation succeeds.</param>
/// <param name="ExpiresAtUtc">The trusted expiry shared by returned options.</param>
/// <param name="Error">A bounded value-free failure token.</param>
public sealed record HumanInputReroutePreparationResult(
    HumanInputSupersedePreparationStatus Status,
    string RequestId,
    IReadOnlyList<HumanInputRerouteCandidateOption> Options,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);
