using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

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

    public ToolAuditMetadataFactory(WorkspacePaths paths, LoopDefinition loopDefinition, IReadOnlyList<ToolCommand> availableCommands)
    {
        _paths = paths;
        _loopDefinition = loopDefinition;
        _availableCommands = availableCommands;
    }

    public Dictionary<string, object?> CreateBase(string requestId, ToolRequest request, ToolPermissionCheck check)
    {
        var metadata = CreateBase(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation.MatchedPath);
        AddPermissionPolicyHash(metadata, check.PolicyHash);
        return metadata;
    }

    public Dictionary<string, object?> CreateBase(string requestId, ToolRequest request, string resolvedPath, FileSystemOperation operation, string matchedPath)
    {
        var metadata = new Dictionary<string, object?>
        {
            [RequestId] = requestId,
            [Command] = ToolCommandFormatter.Format(request.Command),
            [TargetPath] = request.TargetPath,
            [ResolvedPath] = resolvedPath,
            [WorkspaceRoot] = _paths.RootPath,
            [FileSystemOperation] = operation.ToString().ToLowerInvariant(),
            [MatchedPath] = matchedPath,
            [LoopId] = _loopDefinition.Id,
            [RoleId] = _loopDefinition.RoleId,
            [LoopTrigger] = _loopDefinition.Trigger.ToString()
        };
        AddCorrelation(metadata, request);
        return metadata;
    }

    public Dictionary<string, object?> CreateLoopAuthority(string requestId, ToolRequest request, string resolvedPath)
    {
        var metadata = new Dictionary<string, object?>
        {
            [RequestId] = requestId,
            [Command] = ToolCommandFormatter.Format(request.Command),
            [TargetPath] = request.TargetPath,
            [ResolvedPath] = resolvedPath,
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

    public static Dictionary<string, object?> ForError(Exception exception)
    {
        return new Dictionary<string, object?> { [ErrorType] = exception.GetType().Name };
    }

    public static void AddApprovedByHuman(Dictionary<string, object?> metadata, bool approvedByHuman)
    {
        metadata[ApprovedByHuman] = approvedByHuman;
    }

    public static void AddPermissionPolicyHash(Dictionary<string, object?> metadata, string? policyHash)
    {
        metadata[PermissionPolicyHash] = policyHash;
    }

    public static void AddDecision(Dictionary<string, object?> metadata, string decisionBy, string? policyHash)
    {
        metadata[DecisionBy] = decisionBy;
        AddPermissionPolicyHash(metadata, policyHash);
    }

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
