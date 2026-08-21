using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution.Effects;

/// <summary>Converts effect attempts to and from one strict canonical value-free persistence shape.</summary>
public static class GovernedLoopEffectAttemptRecordCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Serializes a validated attempt into canonical compact UTF-8 JSON.</summary>
    public static byte[] Encode(GovernedLoopEffectAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var error = GovernedLoopEffectAttemptContract.Validate(attempt);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(attempt));
        }

        return JsonSerializer.SerializeToUtf8Bytes(ToRecord(attempt), _options);
    }

    /// <summary>Strictly decodes canonical schema-1 UTF-8 JSON into a defensively reconstructed attempt.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> utf8Json, out GovernedLoopEffectAttempt? attempt, out string? reasonCode)
    {
        attempt = null;
        reasonCode = "effect-attempt-record-malformed";
        if (utf8Json.IsEmpty || utf8Json.Length > GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes)
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<GovernedLoopEffectAttemptRecord>(utf8Json, _options);
            if (record is null || !utf8Json.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record, _options)))
            {
                return false;
            }
            if (!TryFromRecord(record, out attempt))
            {
                return false;
            }

            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            attempt = null;
            return false;
        }
    }

    private static GovernedLoopEffectAttemptRecord ToRecord(GovernedLoopEffectAttempt attempt)
        => new(
            attempt.SchemaVersion,
            attempt.Binding.RunId,
            attempt.Binding.Revision.SchemaVersion,
            attempt.Binding.Revision.GraphId,
            attempt.Binding.Revision.RevisionId,
            attempt.Binding.Revision.ExecutableHash,
            attempt.Binding.ExecutionGeneration,
            attempt.NodeId,
            attempt.NodeAttempt,
            attempt.Capability.Id.Value,
            attempt.Capability.Version.Value,
            attempt.Capability.Hash.Value,
            attempt.Implementation.ProviderId.Value,
            attempt.Implementation.ImplementationId,
            attempt.ActuatorOperationId,
            attempt.OperationDescriptorHash,
            attempt.InputFingerprint,
            attempt.TargetFingerprint,
            attempt.PreconditionEvidenceHash,
            attempt.AdmissionAuthorityEvidenceHash,
            attempt.DispatchAuthorityEvidenceHash,
            attempt.BeforeEvidenceId,
            attempt.AfterEvidenceId,
            attempt.Payload.EffectId,
            attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration,
            attempt.Payload.IntentHash,
            attempt.Payload.Phase,
            attempt.Payload.Outcome,
            attempt.Payload.EvidenceStatus,
            attempt.Payload.OutcomeEvidenceId,
            attempt.Payload.ReconciliationEvidenceId,
            attempt.Payload.UpdatedAtUtc,
            attempt.PreviousContentHash,
            attempt.ContentHash);

    private static bool TryFromRecord(GovernedLoopEffectAttemptRecord record, out GovernedLoopEffectAttempt? attempt)
    {
        attempt = null;
        if (!CapabilityId.TryParse(record.CapabilityId, out var capabilityId, out _)
            || !CapabilityVersion.TryParse(record.CapabilityVersion, out var capabilityVersion, out _)
            || !CapabilityDescriptorHash.TryParse(record.CapabilityDescriptorHash, out var descriptorHash, out _)
            || !CapabilityProviderId.TryParse(record.ProviderId, out var providerId, out _))
        {
            return false;
        }

        var revision = GovernedLoopRevisionReference.Create(
            record.RevisionSchemaVersion,
            record.GraphId,
            record.RevisionId,
            record.ExecutableHash);
        var binding = GovernedLoopExecutionBinding.Create(
            record.SchemaVersion,
            record.RunId,
            revision,
            record.ExecutionGeneration);
        var payload = GovernedLoopEffectPayload.Create(
            record.SchemaVersion,
            record.EffectId,
            record.IdempotencyOperationId,
            record.EffectGeneration,
            EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOrigin.Actuator,
            record.NodeId,
            record.IntentHash,
            record.Phase,
            record.Outcome,
            record.EvidenceStatus,
            record.OutcomeEvidenceId,
            record.ReconciliationEvidenceId,
            record.UpdatedAtUtc);
        var candidate = new GovernedLoopEffectAttempt(
            record.SchemaVersion,
            binding,
            record.NodeId,
            record.NodeAttempt,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, record.ImplementationId),
            record.ActuatorOperationId,
            record.OperationDescriptorHash,
            record.InputFingerprint,
            record.TargetFingerprint,
            record.PreconditionEvidenceHash,
            record.AdmissionAuthorityEvidenceHash,
            record.DispatchAuthorityEvidenceHash,
            record.BeforeEvidenceId,
            record.AfterEvidenceId,
            payload,
            record.PreviousContentHash,
            record.ContentHash);
        if (GovernedLoopEffectAttemptContract.Validate(candidate) is not null)
        {
            return false;
        }

        attempt = candidate;
        return true;
    }
}
