using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Records one privacy-safe append-only posture boundary for an exact Human Input waiting checkpoint.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="Sequence">The positive contiguous evidence sequence.</param>
/// <param name="Kind">The closed posture boundary represented by this entry.</param>
/// <param name="OccurredAtUtc">The trusted UTC boundary time.</param>
/// <param name="AnswerSelection">The exact privacy-safe answer selection, present only for <see cref="GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered"/>.</param>
/// <param name="SupersedingCheckpointId">The distinct replacing checkpoint identifier, present only for <see cref="GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Superseded"/>.</param>
/// <param name="SupersedingCheckpointHash">The canonical state hash of the distinct replacing checkpoint, present only for <see cref="GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Superseded"/>.</param>
/// <param name="TerminalizationReceiptId">The opaque later-runner receipt identity, present only for <see cref="GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized"/>.</param>
/// <param name="TerminalizationReceiptHash">The opaque canonical hash of <paramref name="TerminalizationReceiptId"/>, present only for <see cref="GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized"/>.</param>
/// <param name="PreviousEvidenceHash">The exact prior evidence hash, or empty only for sequence one.</param>
/// <param name="EvidenceHash">The canonical evidence hash over every non-self-referential field.</param>
public sealed record GovernedLoopHumanInputWaitingCheckpointEvidence(
    int SchemaVersion,
    long Sequence,
    GovernedLoopHumanInputWaitingCheckpointEvidenceKind Kind,
    DateTimeOffset OccurredAtUtc,
    HumanInputResponseSelectionReference? AnswerSelection,
    string? SupersedingCheckpointId,
    string? SupersedingCheckpointHash,
    string? TerminalizationReceiptId,
    string? TerminalizationReceiptHash,
    string PreviousEvidenceHash,
    string EvidenceHash);
