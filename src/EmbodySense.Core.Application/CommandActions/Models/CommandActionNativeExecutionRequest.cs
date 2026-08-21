using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Supplies one exact prepared command generation to the native isolated host.</summary>
/// <param name="Registration">The exact server registration.</param>
/// <param name="Input">The canonical typed input.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="IdempotencyOperationId">The stable operation identity.</param>
/// <param name="EffectGeneration">The exact effect generation.</param>
/// <param name="TargetFingerprint">The exact prepared target fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The exact prepared precondition hash.</param>
/// <param name="BeforeEvidenceId">The exact retained preparation evidence reference.</param>
public sealed record CommandActionNativeExecutionRequest(
    CommandActionRegistration Registration,
    GovernedActuatorInputEvidence Input,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string TargetFingerprint,
    string PreconditionEvidenceHash,
    string BeforeEvidenceId);
