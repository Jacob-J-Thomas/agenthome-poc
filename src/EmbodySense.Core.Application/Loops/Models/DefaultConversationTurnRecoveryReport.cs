namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Reports restart reconciliation across all incomplete default-conversation turns.
/// </summary>
/// <param name="Results">The deterministic per-turn dispositions.</param>
/// <param name="PreserveCurrentConversation">Whether startup must retain and hydrate the recovered current transcript.</param>
public sealed record DefaultConversationTurnRecoveryReport(
    IReadOnlyList<DefaultConversationTurnRecoveryResult> Results,
    bool PreserveCurrentConversation);
