using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Declares one immutable server-owned structured actuator operation without duplicating capability lifecycle truth.</summary>
/// <param name="SchemaVersion">The descriptor schema, which must be 1.</param>
/// <param name="Capability">The exact capability descriptor identity that owns the operation.</param>
/// <param name="Implementation">The exact server implementation identity.</param>
/// <param name="OperationId">The stable operation identity within the owning actuator.</param>
/// <param name="RiskSummary">Bounded non-sensitive operator-facing risk metadata.</param>
/// <param name="TargetSemantics">How the operation binds its exact target.</param>
/// <param name="Idempotency">The operation's external idempotency posture.</param>
/// <param name="RequiresOptimisticPrecondition">Whether dispatch requires exact optimistic-precondition evidence.</param>
/// <param name="Approval">The operation's separate approval posture.</param>
/// <param name="UnattendedEligible">Whether an otherwise-authorized run may invoke the operation unattended.</param>
/// <param name="Cancellation">The operation's boundary-aware cancellation posture.</param>
/// <param name="Ambiguity">The operation's missing-outcome posture.</param>
/// <param name="RequiresBeforeEvidence">Whether a bounded before-state evidence reference is required.</param>
/// <param name="RequiresAfterEvidence">Whether a bounded after-state evidence reference is required.</param>
/// <param name="RequiresOutcomeEvidence">Whether every conclusive outcome requires a bounded evidence reference.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 hash of all preceding fields.</param>
public sealed record GovernedActuatorOperationDescriptor(
    int SchemaVersion,
    CapabilityDescriptorIdentity Capability,
    CapabilityImplementationIdentity Implementation,
    string OperationId,
    string RiskSummary,
    GovernedActuatorTargetSemantics TargetSemantics,
    GovernedActuatorIdempotencyPosture Idempotency,
    bool RequiresOptimisticPrecondition,
    GovernedActuatorApprovalPosture Approval,
    bool UnattendedEligible,
    GovernedActuatorCancellationPosture Cancellation,
    GovernedActuatorAmbiguityPosture Ambiguity,
    bool RequiresBeforeEvidence,
    bool RequiresAfterEvidence,
    bool RequiresOutcomeEvidence,
    string ContentHash);
