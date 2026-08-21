using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Creates canonical, correlation-rich metadata shared by tool governance audit events.
/// </summary>
internal sealed class ToolAuditMetadataFactory
{
    private const string RequestId = "request_id";
    private const string Command = "command";
    private const string TargetPath = "target_path";
    private const string ResolvedPath = "resolved_path";
    private const string WorkspaceRoot = "workspace_root";
    private const string FileSystemOperation = "filesystem_operation";
    private const string MatchedPath = "matched_path";
    private const string LoopId = "loop_id";
    private const string RoleId = "role_id";
    private const string LoopTrigger = "loop_trigger";
    private const string PermissionPolicyHash = "permission_policy_hash";
    private const string ApprovedByHuman = "approved_by_human";
    private const string DecisionBy = "decision_by";
    private const string ErrorType = "error_type";
    private const string RequiredCapability = "required_capability";
    private const string FallbackCapability = "fallback_capability";
    private const string AvailableCommands = "available_commands";
    private const string LoopCapabilityIdsMetadata = "loop_capability_ids";
    private const string ToolRequestCorrelationId = "tool_request_correlation_id";
    private const string RunId = "run_id";
    private const string DefinitionVersion = "definition_version";
    private const string DefinitionHash = "definition_hash";
    private const string Iteration = "iteration";
    private const string StepId = "step_id";
    private const string Attempt = "attempt";
    private const string AttemptCorrelationId = "attempt_correlation_id";
    private const string AdmittedCommands = "admitted_commands";
    private const string CurrentRoleCommands = "current_role_commands";
    private const string EffectiveCommands = "effective_commands";
    private const string RoleCeilingHash = "role_ceiling_hash";
    private const string CatalogHash = "catalog_hash";

    private readonly WorkspacePaths _paths;
    private readonly LoopDefinition _loopDefinition;
    private readonly IReadOnlyList<ToolCommand> _availableCommands;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolAuditMetadataFactory"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="availableCommands">The available commands.</param>
    public ToolAuditMetadataFactory(WorkspacePaths paths, LoopDefinition loopDefinition, IReadOnlyList<ToolCommand> availableCommands)
    {
        _paths = paths;
        _loopDefinition = loopDefinition;
        _availableCommands = availableCommands;
    }

    /// <summary>
    /// Creates base audit metadata from a resolved permission check.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="request">The request.</param>
    /// <param name="check">The check.</param>
    /// <returns>Canonical request, path, loop, permission-policy, and correlation metadata.</returns>
    public Dictionary<string, object?> CreateBase(string requestId, ToolRequest request, ToolPermissionCheck check)
    {
        var metadata = CreateBase(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation.MatchedPath);
        AddPermissionPolicyHash(metadata, check.PolicyHash);
        return metadata;
    }

    /// <summary>
    /// Creates base audit metadata from explicit path and operation evidence.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="matchedPath">The matched path.</param>
    /// <returns>Canonical request, path, loop, and correlation metadata.</returns>
    public Dictionary<string, object?> CreateBase(string requestId, ToolRequest request, string resolvedPath, FileSystemOperation operation, string matchedPath)
    {
        var mutation = WorkspaceMutationEvidenceProjection.IsMutation(request.Command);
        var evidenceRequest = WorkspaceMutationEvidenceProjection.ProjectRequest(request);
        var metadata = new Dictionary<string, object?>
        {
            [RequestId] = requestId,
            [Command] = ToolCommandFormatter.Format(request.Command),
            [TargetPath] = evidenceRequest.TargetPath,
            [ResolvedPath] = WorkspaceMutationEvidenceProjection.ProjectResolvedTarget(request, resolvedPath),
            [WorkspaceRoot] = mutation ? null : _paths.RootPath,
            [FileSystemOperation] = operation.ToString().ToLowerInvariant(),
            [MatchedPath] = mutation ? null : matchedPath,
            [LoopId] = _loopDefinition.Id,
            [RoleId] = _loopDefinition.RoleId,
            [LoopTrigger] = _loopDefinition.Trigger.ToString()
        };
        AddCorrelation(metadata, request);
        return metadata;
    }

    /// <summary>
    /// Creates metadata for a loop-capability authority decision.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <returns>The required capabilities, active loop authority, and request correlation.</returns>
    public Dictionary<string, object?> CreateLoopAuthority(string requestId, ToolRequest request, string resolvedPath)
    {
        var evidenceRequest = WorkspaceMutationEvidenceProjection.ProjectRequest(request);
        var metadata = new Dictionary<string, object?>
        {
            [RequestId] = requestId,
            [Command] = ToolCommandFormatter.Format(request.Command),
            [TargetPath] = evidenceRequest.TargetPath,
            [ResolvedPath] = WorkspaceMutationEvidenceProjection.ProjectResolvedTarget(request, resolvedPath),
            [LoopId] = _loopDefinition.Id,
            [RoleId] = _loopDefinition.RoleId,
            [LoopTrigger] = _loopDefinition.Trigger.ToString(),
            [RequiredCapability] = LoopCapabilityIds.WorkspaceCommandFor(request.Command),
            [FallbackCapability] = LoopCapabilityIds.WorkspaceCommand,
            [AvailableCommands] = string.Join(",", _availableCommands.Select(ToolCommandFormatter.Format)),
            [LoopCapabilityIdsMetadata] = string.Join(",", _loopDefinition.CapabilityIds)
        };
        AddCorrelation(metadata, request);
        return metadata;
    }

    /// <summary>
    /// Creates non-sensitive failure metadata from an exception.
    /// </summary>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <returns>Metadata containing the exception type.</returns>
    public static Dictionary<string, object?> ForError(Exception exception)
    {
        return new Dictionary<string, object?> { [ErrorType] = exception.GetType().Name };
    }

    /// <summary>
    /// Records whether the request crossed an explicit human approval boundary.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <param name="approvedByHuman">Whether a human explicitly approved the operation.</param>
    public static void AddApprovedByHuman(Dictionary<string, object?> metadata, bool approvedByHuman)
    {
        metadata[ApprovedByHuman] = approvedByHuman;
    }

    /// <summary>
    /// Adds the deterministic permission-policy evidence hash.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <param name="policyHash">The hash of the policy used for the decision.</param>
    public static void AddPermissionPolicyHash(Dictionary<string, object?> metadata, string? policyHash)
    {
        metadata[PermissionPolicyHash] = policyHash;
    }

    /// <summary>
    /// Adds decision provenance and its permission-policy evidence hash.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <param name="decisionBy">The actor or mechanism that made the decision.</param>
    /// <param name="policyHash">The hash of the policy used for the decision.</param>
    public static void AddDecision(Dictionary<string, object?> metadata, string decisionBy, string? policyHash)
    {
        metadata[DecisionBy] = decisionBy;
        AddPermissionPolicyHash(metadata, policyHash);
    }

    /// <summary>
    /// Merges executor-supplied evidence into canonical audit metadata.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <param name="executionMetadata">The execution evidence to merge; matching keys replace earlier values.</param>
    public static void MergeExecution(Dictionary<string, object?> metadata, IReadOnlyDictionary<string, object?> executionMetadata)
    {
        foreach (var item in executionMetadata)
        {
            metadata[item.Key] = item.Value;
        }
    }

    private static void AddCorrelation(Dictionary<string, object?> metadata, ToolRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            metadata[ToolRequestCorrelationId] = request.CorrelationId;
        }

        if (request.AuditCorrelation is not { } correlation)
        {
            return;
        }

        metadata[RunId] = correlation.RunId;
        metadata[LoopId] = correlation.LoopId;
        metadata[RoleId] = correlation.RoleId;
        metadata[DefinitionVersion] = correlation.DefinitionVersion;
        metadata[DefinitionHash] = correlation.DefinitionHash;
        metadata[Iteration] = correlation.Iteration;
        metadata[StepId] = correlation.StepId;
        metadata[Attempt] = correlation.Attempt;
        metadata[AttemptCorrelationId] = correlation.AttemptCorrelationId;
        metadata[AdmittedCommands] = correlation.AdmittedCommands;
        metadata[CurrentRoleCommands] = correlation.CurrentRoleCommands;
        metadata[EffectiveCommands] = correlation.EffectiveCommands;
        metadata[RoleCeilingHash] = correlation.RoleCeilingHash;
        metadata[CatalogHash] = correlation.CatalogHash;
    }
}
