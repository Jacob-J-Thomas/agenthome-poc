using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Retains one value-free, hash-chained actuator effect attempt bound to exact execution and catalog identities.</summary>
/// <param name="SchemaVersion">The attempt schema, which must be 1.</param>
/// <param name="Binding">The exact admitted run, immutable revision, and execution generation.</param>
/// <param name="NodeId">The exact originating node.</param>
/// <param name="NodeAttempt">The exact positive node attempt.</param>
/// <param name="Capability">The exact admitted capability descriptor identity.</param>
/// <param name="Implementation">The exact admitted implementation identity.</param>
/// <param name="ActuatorOperationId">The stable server-owned operation identity.</param>
/// <param name="OperationDescriptorHash">The exact operation metadata hash.</param>
/// <param name="InputFingerprint">The canonical input SHA-256 fingerprint; raw input is never retained here.</param>
/// <param name="TargetFingerprint">The canonical server-resolved target SHA-256 fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The optional optimistic-precondition evidence hash.</param>
/// <param name="AdmissionAuthorityEvidenceHash">The exact admission receipt hash that bounded initial intent.</param>
/// <param name="DispatchAuthorityEvidenceHash">The fresh exact authority-decision hash proved immediately before dispatch.</param>
/// <param name="BeforeEvidenceId">The optional bounded value-free before-state evidence reference.</param>
/// <param name="AfterEvidenceId">The optional bounded value-free after-state evidence reference.</param>
/// <param name="Payload">The canonical executor-neutral effect state.</param>
/// <param name="PreviousContentHash">The prior attempt-version hash for a successor.</param>
/// <param name="ContentHash">The canonical hash of the complete attempt version except this field.</param>
public sealed record GovernedLoopEffectAttempt(
    int SchemaVersion,
    GovernedLoopExecutionBinding Binding,
    string NodeId,
    int NodeAttempt,
    CapabilityDescriptorIdentity Capability,
    CapabilityImplementationIdentity Implementation,
    string ActuatorOperationId,
    string OperationDescriptorHash,
    string InputFingerprint,
    string TargetFingerprint,
    string? PreconditionEvidenceHash,
    string AdmissionAuthorityEvidenceHash,
    string? DispatchAuthorityEvidenceHash,
    string? BeforeEvidenceId,
    string? AfterEvidenceId,
    GovernedLoopEffectPayload Payload,
    string? PreviousContentHash,
    string ContentHash)
{
    /// <summary>Gets a defensively reconstructed execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; } = Binding is null
        ? null!
        : GovernedLoopExecutionBinding.Create(Binding.SchemaVersion, Binding.RunId, Binding.Revision, Binding.ExecutionGeneration);
}
