namespace EmbodySense.Core.Application.Loops.Wait.Models;

/// <summary>Requests canonical ordered re-entry for one exact resumed Wait activation.</summary>
public sealed record GovernedLoopWaitOrderedResumeRequest(
    GovernedLoopWaitOrderedContext Context,
    int ActivationOrdinal,
    string ContinuationEvidenceHash,
    string Actor);
