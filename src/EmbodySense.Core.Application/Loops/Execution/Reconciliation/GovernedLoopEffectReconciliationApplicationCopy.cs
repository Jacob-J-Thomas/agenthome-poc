using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationApplicationCopy
{
    internal static GovernedLoopEffectAttempt? Copy(GovernedLoopEffectAttempt? value)
        => value is null
            ? null
            : new GovernedLoopEffectAttempt(
                value.SchemaVersion,
                value.Binding,
                value.NodeId,
                value.NodeAttempt,
                new CapabilityDescriptorIdentity(value.Capability.Id, value.Capability.Version, value.Capability.Hash),
                new CapabilityImplementationIdentity(value.Implementation.ProviderId, value.Implementation.ImplementationId),
                value.ActuatorOperationId,
                value.OperationDescriptorHash,
                value.InputFingerprint,
                value.TargetFingerprint,
                value.PreconditionEvidenceHash,
                value.AdmissionAuthorityEvidenceHash,
                value.DispatchAuthorityEvidenceHash,
                value.BeforeEvidenceId,
                value.AfterEvidenceId,
                GovernedLoopEffectPayload.Create(
                    value.Payload.SchemaVersion,
                    value.Payload.EffectId,
                    value.Payload.OperationId,
                    value.Payload.EffectGeneration,
                    value.Payload.Origin,
                    value.Payload.OriginNodeId,
                    value.Payload.IntentHash,
                    value.Payload.Phase,
                    value.Payload.Outcome,
                    value.Payload.EvidenceStatus,
                    value.Payload.OutcomeEvidenceId,
                    value.Payload.ReconciliationEvidenceId,
                    value.Payload.UpdatedAtUtc),
                value.PreviousContentHash,
                value.ContentHash);

    internal static GovernedActuatorInputEvidence? Copy(GovernedActuatorInputEvidence? value)
        => value is null ? null : new GovernedActuatorInputEvidence(value.CanonicalJson, value.Fingerprint, value.Utf8ByteCount, value.ElementCount);

    internal static GovernedLoopFrontierPosture? Copy(GovernedLoopFrontierPosture? value)
        => value is null
            ? null
            : GovernedLoopFrontierPosture.Create(value.Binding, value.WorkspaceId, value.GraphArtifactHash, value.GraphLayoutHash, value.AdmissionReceiptHash, value.Payload);
}
