using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Describes the deterministic restart disposition of one incomplete turn.
/// </summary>
/// <param name="TurnId">The stable turn identity.</param>
/// <param name="RunId">The stable run identity.</param>
/// <param name="Classification">The proved crash window.</param>
/// <param name="OriginalCheckpoint">The checkpoint observed at restart.</param>
/// <param name="TerminalStatus">The resulting terminal run status.</param>
/// <param name="Detail">The actionable evidence summary.</param>
public sealed record DefaultConversationTurnRecoveryResult(
    string TurnId,
    string RunId,
    DefaultConversationTurnRecoveryClassification Classification,
    DefaultConversationTurnCheckpoint OriginalCheckpoint,
    LoopRunStatus TerminalStatus,
    string Detail);
