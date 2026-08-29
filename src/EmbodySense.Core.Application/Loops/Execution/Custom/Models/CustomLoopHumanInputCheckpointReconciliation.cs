using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Retains the exact no-retry disposition of one Human Input checkpoint convergence pass.</summary>
internal sealed record CustomLoopHumanInputCheckpointReconciliation(
    CustomLoopHumanInputCheckpointReconciliationStatus Status,
    CustomLoopRunRecord? Run,
    string Detail);
