using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Audit.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

public sealed class ToolBroker : IToolBroker
{
    private readonly WorkspacePaths _paths;
    private readonly IToolPermissionService _permissionService;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly ILocalWorkspaceClient _workspaceClient;
    private readonly IAuditLog _auditLog;

    public ToolBroker(
        WorkspacePaths paths,
        IToolPermissionService permissionService,
        IToolApprovalPrompt approvalPrompt,
        ILocalWorkspaceClient workspaceClient,
        IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissionService);
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentNullException.ThrowIfNull(workspaceClient);
        ArgumentNullException.ThrowIfNull(auditLog);

        _paths = paths;
        _permissionService = permissionService;
        _approvalPrompt = approvalPrompt;
        _workspaceClient = workspaceClient;
        _auditLog = auditLog;
    }

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid().ToString("N");
        var check = _permissionService.Evaluate(request);

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
                ToolCommand.List => await _workspaceClient.ListAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Read => await _workspaceClient.ReadAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Search => await _workspaceClient.SearchAsync(check.ResolvedPath, request.Pattern ?? request.Content, cancellationToken),
                ToolCommand.Append => await _workspaceClient.AppendAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Write => await _workspaceClient.WriteAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Delete => await _workspaceClient.DeleteAsync(check.ResolvedPath, cancellationToken),
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

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, ToolPermissionCheck check)
    {
        return BaseMetadata(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation.MatchedPath);
    }

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, string resolvedPath, FileSystemOperation operation, string matchedPath)
    {
        return new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = FormatCommand(request.Command),
            ["target_path"] = request.TargetPath,
            ["resolved_path"] = resolvedPath,
            ["workspace_root"] = _paths.RootPath,
            ["filesystem_operation"] = operation.ToString().ToLowerInvariant(),
            ["matched_path"] = matchedPath
        };
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
