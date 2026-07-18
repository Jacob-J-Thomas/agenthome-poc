using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Tools;
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
    private readonly IToolGovernanceObserver? _governanceObserver;
    private readonly ToolAuditMetadataFactory _auditMetadataFactory;

    public ToolBroker(
        WorkspacePaths paths,
        IToolPermissionService permissionService,
        IToolApprovalPrompt approvalPrompt,
        IWorkspaceToolExecutor workspaceToolExecutor,
        IAuditLog auditLog,
        LoopDefinition loopDefinition,
        IToolGovernanceObserver? governanceObserver = null)
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
        _governanceObserver = governanceObserver;
        AvailableCommands = GetAvailableCommands(_loopDefinition);
        _auditMetadataFactory = new ToolAuditMetadataFactory(_paths, _loopDefinition, AvailableCommands);
    }

    public IReadOnlyList<ToolCommand> AvailableCommands { get; }

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid().ToString("N");
        if (!IsCommandAvailable(request.Command))
        {
            await RecordLoopAuthorityAsync(requestId, request, request.TargetPath, AuditSchema.Outcomes.Denied, cancellationToken);
            var detail = $"Active loop `{_loopDefinition.Id}` does not grant `{LoopCapabilityIds.WorkspaceCommandFor(request.Command)}` or `{LoopCapabilityIds.WorkspaceCommand}`.";
            var evidence = AuthorityDenied(detail);
            await ObserveDecisionAsync(requestId, request, request.TargetPath, evidence, cancellationToken);
            var result = new ToolResult(ToolExecutionOutcome.Denied, $"denied: {detail}", requestId, request.TargetPath, request, evidence);
            await ObserveOutcomeAsync(result, cancellationToken);
            return result;
        }

        var check = _permissionService.Evaluate(request);

        await RecordLoopAuthorityAsync(requestId, request, check.ResolvedPath, AuditSchema.Outcomes.Allowed, cancellationToken);
        await RecordPermissionAsync(requestId, request, check, cancellationToken);

        if (check.Evaluation.Decision == PermissionDecision.Deny)
        {
            var evidence = DecisionEvidence(check, ToolApprovalDecision.NotEvaluated, null);
            await ObserveDecisionAsync(requestId, request, check.ResolvedPath, evidence, cancellationToken);
            var result = new ToolResult(ToolExecutionOutcome.Denied, $"denied: {check.Evaluation.Detail}", requestId, check.ResolvedPath, request, evidence);
            await ObserveOutcomeAsync(result, cancellationToken);
            await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.Denied, new Dictionary<string, object?>(), cancellationToken);
            return result;
        }

        var approvedByHuman = false;
        ToolApprovalResponse? approvalResponse = null;

        if (check.Evaluation.Decision == PermissionDecision.RequiresApproval)
        {
            var approvalRequest = new ToolApprovalRequest(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation, check.PolicyHash);
            await RecordApprovalRequestAsync(approvalRequest, cancellationToken);
            await ObserveApprovalRequestAsync(requestId, request, check.ResolvedPath, DecisionEvidence(check, ToolApprovalDecision.Requested, null), cancellationToken);
            approvalResponse = await _approvalPrompt.RequestApprovalAsync(approvalRequest, cancellationToken);
            await RecordApprovalDecisionAsync(approvalRequest, approvalResponse, cancellationToken);

            if (!approvalResponse.Approved)
            {
                var evidence = DecisionEvidence(check, ToolApprovalDecision.Rejected, approvalResponse);
                await ObserveDecisionAsync(requestId, request, check.ResolvedPath, evidence, cancellationToken);
                var result = new ToolResult(ToolExecutionOutcome.ApprovalRejected, $"rejected: {approvalResponse.Detail}", requestId, check.ResolvedPath, request, evidence);
                await ObserveOutcomeAsync(result, cancellationToken);
                await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.ApprovalRejected, new Dictionary<string, object?>(), cancellationToken);
                return result;
            }

            approvedByHuman = true;
        }

        var approvalDecision = approvedByHuman ? ToolApprovalDecision.Approved : ToolApprovalDecision.NotRequired;
        var authorizedEvidence = DecisionEvidence(check, approvalDecision, approvalResponse);
        await RecordExecutionIntentAsync(requestId, request, check, approvedByHuman, cancellationToken);
        await ObserveDecisionAsync(requestId, request, check.ResolvedPath, authorizedEvidence, cancellationToken);
        return await ExecuteAuthorizedAsync(requestId, request, check, approvedByHuman, authorizedEvidence, cancellationToken);
    }

    private async Task<ToolResult> ExecuteAuthorizedAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, ToolGovernanceEvidence governance, CancellationToken cancellationToken)
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

            var result = new ToolResult(ToolExecutionOutcome.Succeeded, output.Text, requestId, check.ResolvedPath, request, governance);
            await ObserveOutcomeAsync(result, cancellationToken);
            await RecordExecutionAsync(requestId, request, check, approvedByHuman, AuditSchema.Outcomes.Succeeded, output.Metadata, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            var result = new ToolResult(ToolExecutionOutcome.Failed, $"failed: {exception.Message}", requestId, check.ResolvedPath, request, governance);
            await ObserveOutcomeAsync(result, cancellationToken);
            await RecordExecutionAsync(
                requestId,
                request,
                check,
                approvedByHuman,
                AuditSchema.Outcomes.Failed,
                ToolAuditMetadataFactory.ForError(exception),
                cancellationToken);
            return result;
        }
    }

    private Task RecordExecutionIntentAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, CancellationToken cancellationToken)
    {
        var metadata = _auditMetadataFactory.CreateBase(requestId, request, check);
        ToolAuditMetadataFactory.AddApprovedByHuman(metadata, approvedByHuman);
        return AppendAuditAsync(AuditEvent.Create(
            AuditSchema.Actors.Tool,
            AuditSchema.Actions.ToolExecutionIntent,
            check.ResolvedPath,
            AuditSchema.Outcomes.Requested,
            $"Authorized {ToolCommandFormatter.Format(request.Command)} workspace observation is ready for execution.",
            metadata), cancellationToken);
    }

    private Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken)
    {
        return _governanceObserver?.ObserveDecisionAsync(requestId, request, resolvedPath, evidence, cancellationToken) ?? Task.CompletedTask;
    }

    private Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken)
    {
        return _governanceObserver?.ObserveApprovalRequestAsync(requestId, request, resolvedPath, evidence, cancellationToken) ?? Task.CompletedTask;
    }

    private Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken)
    {
        return _governanceObserver?.ObserveOutcomeAsync(result, cancellationToken) ?? Task.CompletedTask;
    }

    private static ToolGovernanceEvidence AuthorityDenied(string detail)
    {
        return new ToolGovernanceEvidence(ToolAuthorityDecision.Denied, detail, null, null, null, null, ToolApprovalDecision.NotEvaluated, null, null);
    }

    private static ToolGovernanceEvidence DecisionEvidence(ToolPermissionCheck check, ToolApprovalDecision approvalDecision, ToolApprovalResponse? approval)
    {
        return new ToolGovernanceEvidence(
            ToolAuthorityDecision.Allowed,
            "The active loop granted the requested workspace command.",
            check.Evaluation.Decision,
            check.Evaluation.MatchedPath,
            check.Evaluation.Detail,
            check.PolicyHash,
            approvalDecision,
            approval?.DecisionBy,
            approval?.Detail);
    }

    private Task RecordPermissionAsync(string requestId, ToolRequest request, ToolPermissionCheck check, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolPermissionEvaluate,
            target: check.ResolvedPath,
            outcome: FormatDecision(check.Evaluation.Decision),
            detail: check.Evaluation.Detail,
            metadata: _auditMetadataFactory.CreateBase(requestId, request, check)), cancellationToken);
    }

    private Task RecordApprovalRequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var metadata = _auditMetadataFactory.CreateBase(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath);
        ToolAuditMetadataFactory.AddPermissionPolicyHash(metadata, request.PermissionPolicyHash);
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolApprovalRequest,
            target: request.ResolvedPath,
            outcome: AuditSchema.Outcomes.Requested,
            detail: request.PermissionEvaluation.Detail,
            metadata: metadata), cancellationToken);
    }

    private Task RecordApprovalDecisionAsync(ToolApprovalRequest request, ToolApprovalResponse response, CancellationToken cancellationToken)
    {
        var metadata = _auditMetadataFactory.CreateBase(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath);
        ToolAuditMetadataFactory.AddDecision(metadata, response.DecisionBy, request.PermissionPolicyHash);

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
        var metadata = _auditMetadataFactory.CreateBase(requestId, request, check);
        ToolAuditMetadataFactory.AddApprovedByHuman(metadata, approvedByHuman);
        ToolAuditMetadataFactory.MergeExecution(metadata, executionMetadata);

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolExecute,
            target: check.ResolvedPath,
            outcome: outcome,
            detail: $"Executed {ToolCommandFormatter.Format(request.Command)} tool request.",
            metadata: metadata), cancellationToken);
    }

    private Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        return _auditLog.AppendAsync(auditEvent, cancellationToken);
    }

    private Task RecordLoopAuthorityAsync(string requestId, ToolRequest request, string resolvedPath, string outcome, CancellationToken cancellationToken)
    {
        var metadata = _auditMetadataFactory.CreateLoopAuthority(requestId, request, resolvedPath);

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolLoopAuthorityEvaluate,
            target: resolvedPath,
            outcome: outcome,
            detail: outcome == AuditSchema.Outcomes.Allowed
                ? $"Loop `{_loopDefinition.Id}` allowed {ToolCommandFormatter.Format(request.Command)} workspace command authority."
                : $"Loop `{_loopDefinition.Id}` denied {ToolCommandFormatter.Format(request.Command)} workspace command authority.",
            metadata: metadata), cancellationToken);
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

}
