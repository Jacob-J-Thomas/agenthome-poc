using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Requests one exact admitted server-registered actuator effect attempt.</summary>
public sealed record GovernedLoopEffectAttemptRequest(
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    GovernedLoopExecutionBinding ExecutionBinding,
    GovernedLoopGraphRevisionArtifact GraphArtifact,
    string NodeId,
    int NodeAttempt,
    CapabilityAdmissionPin CapabilityPin,
    string ActuatorOperationId,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string InputJson,
    AuthorityCeiling RequiredAuthority,
    string CorrelationId)
{
    /// <summary>
    /// Gets the already immutable common authority contract supplied by the caller. The execution service validates its
    /// finite bounds before any catalog, store, or authority port is called.
    /// </summary>
    public AuthorityCeiling RequiredAuthority { get; } = RequiredAuthority;
}
