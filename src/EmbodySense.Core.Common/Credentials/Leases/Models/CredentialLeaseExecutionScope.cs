namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a credential lease to one exact authenticated governed-loop execution.</summary>
public sealed record CredentialLeaseExecutionScope(
    string WorkspaceId,
    string ActorId,
    string ActorAuthenticationEvidenceHash,
    string AttributionEvidenceHash,
    string AdmissionReceiptHash,
    string RunId,
    string GraphId,
    string GraphRevisionId,
    string GraphExecutableHash,
    long ExecutionGeneration,
    string RoleId,
    long RoleRevision,
    string RoleContentHash,
    string LoopId,
    string LoopRevisionId,
    long DeclaredLoopRevision,
    string LoopPublicationHash);
