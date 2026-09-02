using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Converts reconciliation cases to and from one strict canonical schema-1 UTF-8 JSON shape.</summary>
public static class GovernedLoopEffectReconciliationRecordCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = GovernedLoopEffectReconciliationContractLimits.MaxJsonDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Serializes a valid reconciliation case into canonical compact UTF-8 JSON.</summary>
    /// <exception cref="ArgumentException">Thrown when the case is invalid or exceeds the canonical record bound.</exception>
    public static byte[] Encode(GovernedLoopEffectReconciliationCase reconciliationCase)
    {
        ArgumentNullException.ThrowIfNull(reconciliationCase);
        var validation = GovernedLoopEffectReconciliationContractValidator.Validate(reconciliationCase);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Effect reconciliation is invalid at {validation.Errors[0].Path}.", nameof(reconciliationCase));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToRecord(reconciliationCase), _options);
        if (bytes.Length > GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes)
        {
            throw new ArgumentException("The canonical effect-reconciliation record exceeds its finite size bound.", nameof(reconciliationCase));
        }
        return bytes;
    }

    /// <summary>Strictly decodes canonical schema-1 UTF-8 JSON into a defensively reconstructed case.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> utf8Json, out GovernedLoopEffectReconciliationCase? reconciliationCase, out string? reasonCode)
    {
        reconciliationCase = null;
        reasonCode = "effect-reconciliation-record-malformed";
        if (utf8Json.IsEmpty || utf8Json.Length > GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes)
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<GovernedLoopEffectReconciliationRecord>(utf8Json, _options);
            if (record is null || record.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion)
            {
                return false;
            }
            var canonical = JsonSerializer.SerializeToUtf8Bytes(record, _options);
            if (!utf8Json.SequenceEqual(canonical) || canonical.Length > GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes)
            {
                return false;
            }
            if (!TryFromRecord(record, out var parsed) || !GovernedLoopEffectReconciliationContractValidator.Validate(parsed).IsValid)
            {
                reasonCode = "effect-reconciliation-record-invalid";
                return false;
            }

            reconciliationCase = GovernedLoopEffectReconciliationContractCopy.Copy(parsed);
            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            reconciliationCase = null;
            return false;
        }
    }

    private static GovernedLoopEffectReconciliationRecord ToRecord(GovernedLoopEffectReconciliationCase value)
        => new(
            value.SchemaVersion,
            value.CaseId,
            value.CaseVersion,
            value.Binding.WorkspaceId,
            value.Binding.Execution.SchemaVersion,
            value.Binding.Execution.RunId,
            value.Binding.Execution.Revision.SchemaVersion,
            value.Binding.Execution.Revision.GraphId,
            value.Binding.Execution.Revision.RevisionId,
            value.Binding.Execution.Revision.ExecutableHash,
            value.Binding.Execution.ExecutionGeneration,
            value.Binding.NodeId,
            value.Binding.ActivationOrdinal,
            value.Binding.VisitOrdinal,
            value.Binding.NodeAttempt,
            value.Binding.EffectId,
            value.Binding.OperationId,
            value.Binding.EffectGeneration,
            value.Binding.IntentHash,
            value.Binding.CurrentAttemptHash,
            value.Binding.ContentHash,
            value.ContractMetadata.ContractId,
            value.ContractMetadata.ContractVersion,
            value.ContractMetadata.Capability.Id.Value,
            value.ContractMetadata.Capability.Version.Value,
            value.ContractMetadata.Capability.Hash.Value,
            value.ContractMetadata.Implementation.ProviderId.Value,
            value.ContractMetadata.Implementation.ImplementationId,
            value.ContractMetadata.ActuatorOperationId,
            value.ContractMetadata.OperationDescriptorHash,
            value.ContractMetadata.ProbeContractId,
            value.ContractMetadata.ProbeContractVersion,
            value.ContractMetadata.ProbeContractHash,
            value.ContractMetadata.ContentHash,
            value.EvidenceSources,
            value.ObservationHistory,
            value.AssessmentHistory,
            value.CurrentAssessmentHash,
            value.Disposition,
            value.Resolution,
            value.CaseReceiptHashes,
            value.PreviousContentHash,
            value.OpenedAtUtc,
            value.UpdatedAtUtc,
            value.ContentHash);

    private static bool TryFromRecord(GovernedLoopEffectReconciliationRecord record, out GovernedLoopEffectReconciliationCase? reconciliationCase)
    {
        reconciliationCase = null;
        if (!CapabilityId.TryParse(record.CapabilityId, out var capabilityId, out _)
            || !CapabilityVersion.TryParse(record.CapabilityVersion, out var capabilityVersion, out _)
            || !CapabilityDescriptorHash.TryParse(record.CapabilityDescriptorHash, out var descriptorHash, out _)
            || !CapabilityProviderId.TryParse(record.ProviderId, out var providerId, out _))
        {
            return false;
        }

        var revision = GovernedLoopRevisionReference.Create(record.RevisionSchemaVersion, record.GraphId, record.RevisionId, record.ExecutableHash);
        var execution = GovernedLoopExecutionBinding.Create(record.ExecutionSchemaVersion, record.RunId, revision, record.ExecutionGeneration);
        var binding = new GovernedLoopEffectReconciliationBinding(
            record.SchemaVersion,
            record.WorkspaceId,
            execution,
            record.NodeId,
            record.ActivationOrdinal,
            record.VisitOrdinal,
            record.NodeAttempt,
            record.EffectId,
            record.OperationId,
            record.EffectGeneration,
            record.IntentHash,
            record.CurrentAttemptHash,
            record.BindingHash);
        var metadata = new GovernedLoopEffectReconciliationContractMetadata(
            record.SchemaVersion,
            record.ReconciliationContractId,
            record.ReconciliationContractVersion,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, record.ImplementationId),
            record.ActuatorOperationId,
            record.OperationDescriptorHash,
            record.ProbeContractId,
            record.ProbeContractVersion,
            record.ProbeContractHash,
            record.ContractMetadataHash);
        reconciliationCase = new GovernedLoopEffectReconciliationCase(
            record.SchemaVersion,
            record.CaseId,
            record.CaseVersion,
            binding,
            metadata,
            record.EvidenceSources,
            record.ObservationHistory,
            record.AssessmentHistory,
            record.CurrentAssessmentHash,
            record.Disposition,
            record.Resolution,
            record.CaseReceiptHashes,
            record.PreviousContentHash,
            record.OpenedAtUtc,
            record.UpdatedAtUtc,
            record.ContentHash);
        return true;
    }
}
