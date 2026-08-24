using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Loops.Sequential.Models;

/// <summary>Binds one sequential-runtime adapter hand-off to exact admitted and immutable evidence identities.</summary>
/// <param name="SchemaVersion">The binding schema version, which must be 1.</param>
/// <param name="WorkspaceId">The exact canonical workspace scope.</param>
/// <param name="ExecutionBinding">The exact run, graph revision, and execution generation.</param>
/// <param name="AdmissionOperationId">The exact admission idempotency operation.</param>
/// <param name="AdmissionReceipt">The complete exact successful admission proof retained for crash-safe authority checks.</param>
/// <param name="AdmissionReceiptHash">The exact successful admission-receipt hash.</param>
/// <param name="AdmissionRequestHash">The exact caller-stable admission-request hash.</param>
/// <param name="InvocationPayloadHash">The exact immutable invocation-snapshot hash.</param>
/// <param name="GraphArtifactHash">The exact immutable graph-artifact hash.</param>
/// <param name="GraphLayoutHash">The exact immutable graph-layout hash.</param>
/// <param name="CommandActionCapabilityIds">The exact sorted distinct command Action capability roots derived from the immutable graph.</param>
/// <param name="ContentHash">The canonical hash over every preceding field.</param>
/// <remarks>This value links evidence but does not grant, widen, refresh, or re-resolve authority.</remarks>
public sealed record GovernedLoopSequentialAdapterBinding(
    int SchemaVersion,
    string WorkspaceId,
    GovernedLoopExecutionBinding ExecutionBinding,
    string AdmissionOperationId,
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    string AdmissionReceiptHash,
    string AdmissionRequestHash,
    string InvocationPayloadHash,
    string GraphArtifactHash,
    string GraphLayoutHash,
    IReadOnlyList<string> CommandActionCapabilityIds,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental binding schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSequentialContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied exact execution binding.</summary>
    public GovernedLoopExecutionBinding ExecutionBinding { get; } = GovernedLoopSequentialContractCopy.Copy(ExecutionBinding);

    /// <summary>Gets the defensively copied complete immutable admission receipt.</summary>
    public GovernedLoopAdmissionReceipt AdmissionReceipt { get; } = GovernedLoopAdmissionContractCopy.Copy(AdmissionReceipt);

    /// <summary>Gets the defensively copied exact command Action capability-root snapshot.</summary>
    public IReadOnlyList<string> CommandActionCapabilityIds { get; } = CommandActionCapabilityIds is null
        ? null!
        : Array.AsReadOnly(CommandActionCapabilityIds.Take(GovernedLoopSequentialContractLimits.MaxCommandActionCapabilities + 1).ToArray());
}
