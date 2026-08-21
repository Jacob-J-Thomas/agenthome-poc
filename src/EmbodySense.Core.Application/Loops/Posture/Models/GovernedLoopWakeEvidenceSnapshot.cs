using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Carries one sleeping checkpoint and its optional current wake evidence across the posture port.</summary>
/// <param name="Checkpoint">The immutable sleeping checkpoint.</param>
/// <param name="Wake">The current wake evidence, or <see langword="null"/> before any wake claim.</param>
public sealed record GovernedLoopWakeEvidenceSnapshot(GovernedLoopSleepCheckpoint Checkpoint, GovernedLoopWakeEvidence? Wake);
