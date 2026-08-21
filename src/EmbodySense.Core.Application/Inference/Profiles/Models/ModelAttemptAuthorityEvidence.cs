namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns exact current role, loop, node, frontier, primary, and operation revalidation.</summary>
public sealed record ModelAttemptAuthorityEvidence(
    ModelAttemptAuthorityStatus Status,
    string RoutingAdmissionHash,
    string RunId,
    long ExecutionGeneration,
    string OwningRoleId,
    string NodeId,
    string PrimaryPinHash,
    int PlanOrdinal,
    int ActivationOrdinal,
    int VisitOrdinal,
    int AttemptNumber,
    string AttemptOperationId,
    string? EvidenceHash);
