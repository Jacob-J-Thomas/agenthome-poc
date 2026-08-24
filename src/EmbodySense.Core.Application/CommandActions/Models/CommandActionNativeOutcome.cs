namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Returns one conclusive command outcome using only a closed status and durable evidence reference.</summary>
/// <param name="Kind">The conclusive success or failure.</param>
/// <param name="OutcomeEvidenceId">The authenticated retained outcome reference.</param>
public sealed record CommandActionNativeOutcome(CommandActionNativeOutcomeKind Kind, string OutcomeEvidenceId);
