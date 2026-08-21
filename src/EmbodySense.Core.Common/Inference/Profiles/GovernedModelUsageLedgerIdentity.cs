using System.Text.Json;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Keys one provider attempt to exact admission, frontier, profile, and budget identities.</summary>
public sealed class GovernedModelUsageLedgerIdentity
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageLedgerIdentity(string workspaceId, string runId, string graphId, string graphRevisionId, string graphExecutableHash, long executionGeneration, string admissionReceiptHash, string routingAdmissionHash, string authorityEvidenceHash, string dataPostureEvidenceHash, string nodeId, int planOrdinal, int activationOrdinal, int visitOrdinal, string attemptOperationId, int attemptNumber, string profilePinHash, string budgetPolicyHash)
    {
        WorkspaceId = workspaceId;
        RunId = runId;
        GraphId = graphId;
        GraphRevisionId = graphRevisionId;
        GraphExecutableHash = graphExecutableHash;
        ExecutionGeneration = executionGeneration;
        AdmissionReceiptHash = admissionReceiptHash;
        RoutingAdmissionHash = routingAdmissionHash;
        AuthorityEvidenceHash = authorityEvidenceHash;
        DataPostureEvidenceHash = dataPostureEvidenceHash;
        NodeId = nodeId;
        PlanOrdinal = planOrdinal;
        ActivationOrdinal = activationOrdinal;
        VisitOrdinal = visitOrdinal;
        AttemptOperationId = attemptOperationId;
        AttemptNumber = attemptNumber;
        ProfilePinHash = profilePinHash;
        BudgetPolicyHash = budgetPolicyHash;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-usage-ledger-identity.v1", WriteCanonical);
    }

    /// <summary>Gets the canonical physical-workspace scope.</summary>
    public string WorkspaceId { get; }
    /// <summary>Gets the exact admitted run identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the exact immutable graph identity.</summary>
    public string GraphId { get; }
    /// <summary>Gets the exact immutable graph revision.</summary>
    public string GraphRevisionId { get; }
    /// <summary>Gets the exact executable graph hash.</summary>
    public string GraphExecutableHash { get; }
    /// <summary>Gets the exact execution generation.</summary>
    public long ExecutionGeneration { get; }
    /// <summary>Gets the final canonical admission receipt hash.</summary>
    public string AdmissionReceiptHash { get; }
    /// <summary>Gets the embedded model-routing admission hash.</summary>
    public string RoutingAdmissionHash { get; }
    /// <summary>Gets the exact current attempt-authority proof hash.</summary>
    public string AuthorityEvidenceHash { get; }
    /// <summary>Gets the exact current server-owned data-classification proof hash.</summary>
    public string DataPostureEvidenceHash { get; }
    /// <summary>Gets the exact graph node identity.</summary>
    public string NodeId { get; }
    /// <summary>Gets the exact zero-based executable plan coordinate.</summary>
    public int PlanOrdinal { get; }
    /// <summary>Gets the exact zero-based durable frontier activation coordinate.</summary>
    public int ActivationOrdinal { get; }
    /// <summary>Gets the exact positive visit ordinal for this node.</summary>
    public int VisitOrdinal { get; }
    /// <summary>Gets the exact server-owned frontier attempt operation.</summary>
    public string AttemptOperationId { get; }
    /// <summary>Gets the exact positive frontier attempt number.</summary>
    public int AttemptNumber { get; }
    /// <summary>Gets the exact admitted profile/configuration pin hash.</summary>
    public string ProfilePinHash { get; }
    /// <summary>Gets the exact admitted budget-policy hash.</summary>
    public string BudgetPolicyHash { get; }
    /// <summary>Gets the canonical identity hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates an exact provider-attempt ledger identity from owning domain coordinates.</summary>
    public static GovernedModelUsageLedgerIdentity Create(
        int schemaVersion,
        string workspaceId,
        string runId,
        string graphId,
        string graphRevisionId,
        string graphExecutableHash,
        long executionGeneration,
        string admissionReceiptHash,
        string routingAdmissionHash,
        string authorityEvidenceHash,
        string dataPostureEvidenceHash,
        string nodeId,
        int planOrdinal,
        int activationOrdinal,
        int visitOrdinal,
        string attemptOperationId,
        int attemptNumber,
        string profilePinHash,
        string budgetPolicyHash)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId)) throw new ArgumentException("Workspace identity must use the canonical workspace-sha256 scope.", nameof(workspaceId));
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId), GovernedLoopExecutionLimits.MaxIdentifierCharacters);
        CustomLoopArtifactIdentifier.Require(graphId, nameof(graphId));
        CustomLoopArtifactIdentifier.Require(graphRevisionId, nameof(graphRevisionId));
        CustomLoopArtifactIdentifier.Require(nodeId, nameof(nodeId));
        CustomLoopArtifactIdentifier.Require(attemptOperationId, nameof(attemptOperationId), GovernedLoopExecutionLimits.MaxIdentifierCharacters);
        if (executionGeneration is < 1 or > GovernedLoopExecutionLimits.MaxExecutionGeneration) throw new ArgumentOutOfRangeException(nameof(executionGeneration));
        if (planOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes) throw new ArgumentOutOfRangeException(nameof(planOrdinal));
        if (activationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes) throw new ArgumentOutOfRangeException(nameof(activationOrdinal));
        if (visitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits) throw new ArgumentOutOfRangeException(nameof(visitOrdinal));
        if (attemptNumber is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt) throw new ArgumentOutOfRangeException(nameof(attemptNumber));

        return new GovernedModelUsageLedgerIdentity(
            workspaceId,
            runId,
            graphId,
            graphRevisionId,
            GovernedModelContractRules.RequireHash(graphExecutableHash, nameof(graphExecutableHash)),
            executionGeneration,
            GovernedModelContractRules.RequireHash(admissionReceiptHash, nameof(admissionReceiptHash)),
            GovernedModelContractRules.RequireHash(routingAdmissionHash, nameof(routingAdmissionHash)),
            GovernedModelContractRules.RequireHash(authorityEvidenceHash, nameof(authorityEvidenceHash)),
            GovernedModelContractRules.RequireHash(dataPostureEvidenceHash, nameof(dataPostureEvidenceHash)),
            nodeId,
            planOrdinal,
            activationOrdinal,
            visitOrdinal,
            attemptOperationId,
            attemptNumber,
            GovernedModelContractRules.RequireHash(profilePinHash, nameof(profilePinHash)),
            GovernedModelContractRules.RequireHash(budgetPolicyHash, nameof(budgetPolicyHash)));
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("activationOrdinal", ActivationOrdinal);
        writer.WriteString("admissionReceiptHash", AdmissionReceiptHash);
        writer.WriteString("attemptOperationId", AttemptOperationId);
        writer.WriteString("authorityEvidenceHash", AuthorityEvidenceHash);
        writer.WriteNumber("attemptNumber", AttemptNumber);
        writer.WriteString("budgetPolicyHash", BudgetPolicyHash);
        writer.WriteString("dataPostureEvidenceHash", DataPostureEvidenceHash);
        writer.WriteString("graphExecutableHash", GraphExecutableHash);
        writer.WriteString("graphId", GraphId);
        writer.WriteString("graphRevisionId", GraphRevisionId);
        writer.WriteNumber("executionGeneration", ExecutionGeneration);
        writer.WriteString("nodeId", NodeId);
        writer.WriteNumber("planOrdinal", PlanOrdinal);
        writer.WriteString("profilePinHash", ProfilePinHash);
        writer.WriteString("runId", RunId);
        writer.WriteString("routingAdmissionHash", RoutingAdmissionHash);
        writer.WriteNumber("visitOrdinal", VisitOrdinal);
        writer.WriteString("workspaceId", WorkspaceId);
        writer.WriteEndObject();
    }
}
