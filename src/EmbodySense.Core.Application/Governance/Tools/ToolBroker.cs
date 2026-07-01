using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

public sealed class ToolBroker : IToolBroker
{
    private static readonly ToolCommand[] AllCommands = Enum.GetValues<ToolCommand>();
    private readonly WorkspacePaths _paths;
    private readonly IToolPermissionService _permissionService;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly IWorkspaceToolExecutor _workspaceToolExecutor;
    private readonly IAuditLog _auditLog;
    private readonly LoopDefinition _loopDefinition;

    public ToolBroker(
        WorkspacePaths paths,
        IToolPermissionService permissionService,
        IToolApprovalPrompt approvalPrompt,
        IWorkspaceToolExecutor workspaceToolExecutor,
        IAuditLog auditLog,
        LoopDefinition loopDefinition)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissionService);
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentNullException.ThrowIfNull(workspaceToolExecutor);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(loopDefinition);

        _paths = paths;
        _permissionService = permissionService;
        _approvalPrompt = approvalPrompt;
        _workspaceToolExecutor = workspaceToolExecutor;
        _auditLog = auditLog;
        _loopDefinition = loopDefinition;
        AvailableCommands = GetAvailableCommands(_loopDefinition);
    }

    public IReadOnlyList<ToolCommand> AvailableCommands { get; }

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid().ToString("N");
        if (!IsCommandAvailable(request.Command))
        {
            await RecordLoopAuthorityAsync(requestId, request, request.TargetPath, AuditSchema.Outcomes.Denied, cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Denied, $"denied: active loop `{_loopDefinition.Id}` does not grant `{LoopCapabilityIds.WorkspaceCommandFor(request.Command)}` or `{LoopCapabilityIds.WorkspaceCommand}`.", requestId, request.TargetPath, request);
        }

        var check = _permissionService.Evaluate(request);

        await RecordLoopAuthorityAsync(requestId, request, check.ResolvedPath, AuditSchema.Outcomes.Allowed, cancellationToken);
        await RecordPermissionAsync(requestId, request, check, cancellationToken);

        if (check.Evaluation.Decision == PermissionDecision.Deny)
        {
            await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.Denied, new Dictionary<string, object?>(), cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Denied, $"denied: {check.Evaluation.Detail}", requestId, check.ResolvedPath, request);
        }

        var approvedByHuman = false;

        if (check.Evaluation.Decision == PermissionDecision.RequiresApproval)
        {
            var approvalRequest = new ToolApprovalRequest(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation);
            await RecordApprovalRequestAsync(approvalRequest, cancellationToken);
            var approval = await _approvalPrompt.RequestApprovalAsync(approvalRequest, cancellationToken);
            await RecordApprovalDecisionAsync(approvalRequest, approval, cancellationToken);

            if (!approval.Approved)
            {
                await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.ApprovalRejected, new Dictionary<string, object?>(), cancellationToken);
                return new ToolResult(ToolExecutionOutcome.ApprovalRejected, $"rejected: {approval.Detail}", requestId, check.ResolvedPath, request);
            }

            approvedByHuman = true;
        }

        return await ExecuteAuthorizedAsync(requestId, request, check, approvedByHuman, cancellationToken);
    }

    private async Task<ToolResult> ExecuteAuthorizedAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, CancellationToken cancellationToken)
    {
        try
        {
            var output = request.Command switch
            {
                ToolCommand.List => await _workspaceToolExecutor.ListAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Read => await _workspaceToolExecutor.ReadAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Search => await _workspaceToolExecutor.SearchAsync(check.ResolvedPath, request.Pattern ?? request.Content, cancellationToken),
                ToolCommand.Append => await _workspaceToolExecutor.AppendAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Write => await _workspaceToolExecutor.WriteAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Delete => await _workspaceToolExecutor.DeleteAsync(check.ResolvedPath, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "Unsupported tool command.")
            };

            await RecordExecutionAsync(requestId, request, check, approvedByHuman, AuditSchema.Outcomes.Succeeded, output.Metadata, cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Succeeded, output.Text, requestId, check.ResolvedPath, request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await RecordExecutionAsync(
                requestId,
                request,
                check,
                approvedByHuman,
                AuditSchema.Outcomes.Failed,
                new Dictionary<string, object?> { ["error_type"] = exception.GetType().Name },
                cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Failed, $"failed: {exception.Message}", requestId, check.ResolvedPath, request);
        }
    }

    private Task RecordPermissionAsync(string requestId, ToolRequest request, ToolPermissionCheck check, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolPermissionEvaluate,
            target: check.ResolvedPath,
            outcome: FormatDecision(check.Evaluation.Decision),
            detail: check.Evaluation.Detail,
            metadata: BaseMetadata(requestId, request, check)), cancellationToken);
    }

    private Task RecordApprovalRequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolApprovalRequest,
            target: request.ResolvedPath,
            outcome: AuditSchema.Outcomes.Requested,
            detail: request.PermissionEvaluation.Detail,
            metadata: BaseMetadata(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath)), cancellationToken);
    }

    private Task RecordApprovalDecisionAsync(ToolApprovalRequest request, ToolApprovalResponse response, CancellationToken cancellationToken)
    {
        var metadata = BaseMetadata(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath);
        metadata["decision_by"] = response.DecisionBy;

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolApprovalDecision,
            target: request.ResolvedPath,
            outcome: response.Approved ? AuditSchema.Outcomes.Approved : AuditSchema.Outcomes.Rejected,
            detail: response.Detail,
            metadata: metadata), cancellationToken);
    }

    private Task RecordExecutionAsync(
        string requestId,
        ToolRequest request,
        ToolPermissionCheck check,
        bool approvedByHuman,
        string outcome,
        IReadOnlyDictionary<string, object?> executionMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = BaseMetadata(requestId, request, check);
        metadata["approved_by_human"] = approvedByHuman;

        foreach (var item in executionMetadata)
        {
            metadata[item.Key] = item.Value;
        }

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolExecute,
            target: check.ResolvedPath,
            outcome: outcome,
            detail: $"Executed {FormatCommand(request.Command)} tool request.",
            metadata: metadata), cancellationToken);
    }

    private Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        return _auditLog.AppendAsync(auditEvent, cancellationToken);
    }

    private Task RecordLoopAuthorityAsync(string requestId, ToolRequest request, string resolvedPath, string outcome, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = FormatCommand(request.Command),
            ["target_path"] = request.TargetPath,
            ["resolved_path"] = resolvedPath,
            ["loop_id"] = _loopDefinition.Id,
            ["role_id"] = _loopDefinition.RoleId,
            ["loop_trigger"] = _loopDefinition.Trigger.ToString(),
            ["required_capability"] = LoopCapabilityIds.WorkspaceCommandFor(request.Command),
            ["fallback_capability"] = LoopCapabilityIds.WorkspaceCommand,
            ["available_commands"] = string.Join(",", AvailableCommands.Select(FormatCommand)),
            ["loop_capability_ids"] = string.Join(",", _loopDefinition.CapabilityIds)
        };
        AddCorrelationMetadata(metadata, request);

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolLoopAuthorityEvaluate,
            target: resolvedPath,
            outcome: outcome,
            detail: outcome == AuditSchema.Outcomes.Allowed
                ? $"Loop `{_loopDefinition.Id}` allowed {FormatCommand(request.Command)} workspace command authority."
                : $"Loop `{_loopDefinition.Id}` denied {FormatCommand(request.Command)} workspace command authority.",
            metadata: metadata), cancellationToken);
    }

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, ToolPermissionCheck check)
    {
        return BaseMetadata(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation.MatchedPath);
    }

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, string resolvedPath, FileSystemOperation operation, string matchedPath)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = FormatCommand(request.Command),
            ["target_path"] = request.TargetPath,
            ["resolved_path"] = resolvedPath,
            ["workspace_root"] = _paths.RootPath,
            ["filesystem_operation"] = operation.ToString().ToLowerInvariant(),
            ["matched_path"] = matchedPath
        };

        metadata["loop_id"] = _loopDefinition.Id;
        metadata["role_id"] = _loopDefinition.RoleId;
        metadata["loop_trigger"] = _loopDefinition.Trigger.ToString();
        AddCorrelationMetadata(metadata, request);

        return metadata;
    }

    private static void AddCorrelationMetadata(Dictionary<string, object?> metadata, ToolRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            metadata["tool_request_correlation_id"] = request.CorrelationId;
        }
    }

    private bool IsCommandAvailable(ToolCommand command)
    {
        return AvailableCommands.Contains(command);
    }

    private static IReadOnlyList<ToolCommand> GetAvailableCommands(LoopDefinition loopDefinition)
    {
        return AllCommands.Where(command => LoopCapabilityIds.AllowsWorkspaceCommand(loopDefinition.CapabilityIds, command)).ToArray();
    }

    private static string FormatDecision(PermissionDecision decision)
    {
        return decision switch
        {
            PermissionDecision.Allow => AuditSchema.Outcomes.Allowed,
            PermissionDecision.RequiresApproval => AuditSchema.Outcomes.RequiresApproval,
            PermissionDecision.Deny => AuditSchema.Outcomes.Denied,
            _ => AuditSchema.Outcomes.Unknown
        };
    }

    private static string FormatCommand(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }
}
