namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Returns one bounded, exclusive, no-wrap canonical scan page for Human Input continuation discovery.</summary>
/// <param name="Status">The closed page-read disposition.</param>
/// <param name="Candidates">The underfilled bounded candidate projection from one bounded checkpoint-ordinal scan of one canonical run.</param>
/// <param name="NextScanCursor">The opaque exclusive cursor, or null only after an empty clean-tail probe.</param>
/// <param name="HasMoreScanWork">Whether another bounded scan or clean-tail probe remains; it does not describe an underlying source-page truncation.</param>
public sealed record HumanInputResponseContinuationRecoveryPage(
    HumanInputResponseContinuationRecoveryPageStatus Status,
    IReadOnlyList<HumanInputResponseContinuationCandidate> Candidates,
    string? NextScanCursor,
    bool HasMoreScanWork);
