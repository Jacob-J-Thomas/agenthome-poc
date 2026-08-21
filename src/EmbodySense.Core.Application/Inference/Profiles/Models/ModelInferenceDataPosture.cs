using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Preserves server-classified attempt data classes bound to exact frontier coordinates without raw input.</summary>
public sealed record ModelInferenceDataPosture(
    ModelInferenceDataPostureStatus Status,
    string RunId,
    string NodeId,
    int PlanOrdinal,
    int ActivationOrdinal,
    int VisitOrdinal,
    int AttemptNumber,
    string AttemptOperationId,
    string InputPayloadHash,
    IReadOnlyList<CapabilityDataClass> DataClasses,
    string? EvidenceHash)
{
    /// <summary>Gets a defensive copy of the exact classified data classes.</summary>
    public IReadOnlyList<CapabilityDataClass> DataClasses { get; } = ModelProfileApplicationContractCopy.Snapshot(DataClasses, CapabilityContractLimits.MaxDataClasses, nameof(DataClasses));
}
