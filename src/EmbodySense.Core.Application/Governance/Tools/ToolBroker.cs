using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Loops;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Executes workspace commands through loop authority, permission, approval, audit, actuation, and evidence-retention gates.
/// </summary>
/// <remarks>
/// A human approval does not bypass loop capability or directory policy and is revalidated immediately before actuation when
/// dynamic authority is configured. After actuation, retention and audit failures are surfaced as integrity warnings because
/// retrying the workspace operation under a new identifier could duplicate a mutation.
/// </remarks>
public sealed class ToolBroker : IToolBroker
{
    private const int MaxCorrelationCharacters = 512;
    private static readonly ToolCommand[] _allCommands = Enum.GetValues<ToolCommand>();
    private static readonly TimeSpan _defaultPostActuationIntegrityTimeout = TimeSpan.FromSeconds(30);
    private readonly WorkspacePaths _paths;
    private readonly IToolPermissionService _permissionService;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly IWorkspaceToolExecutor _workspaceToolExecutor;
    private readonly IAuditLog _auditLog;
    private readonly LoopDefinition _loopDefinition;
    private readonly ToolResultRetentionService _toolResultRetention;
    private readonly IToolGovernanceObserver? _governanceObserver;
    private readonly IToolActuationAuthorityRevalidator? _actuationAuthorityRevalidator;
    private readonly ToolAuditMetadataFactory _auditMetadataFactory;
    private readonly TimeSpan _postActuationIntegrityTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolBroker"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="permissionService">The permission service.</param>
    /// <param name="approvalPrompt">The approval prompt.</param>
    /// <param name="workspaceToolExecutor">The workspace tool executor.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="toolResultRetentionStore">The tool result retention store.</param>
    /// <param name="governanceObserver">The governance observer.</param>
    /// <param name="actuationAuthorityRevalidator">The actuation authority revalidator.</param>
    /// <param name="postActuationIntegrityTimeout">The post actuation integrity timeout.</param>
    public ToolBroker(
        WorkspacePaths paths,
        IToolPermissionService permissionService,
        IToolApprovalPrompt approvalPrompt,
        IWorkspaceToolExecutor workspaceToolExecutor,
        IAuditLog auditLog,
        LoopDefinition loopDefinition,
        IToolResultRetentionStore toolResultRetentionStore,
        IToolGovernanceObserver? governanceObserver = null,
        IToolActuationAuthorityRevalidator? actuationAuthorityRevalidator = null,
        TimeSpan? postActuationIntegrityTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissionService);
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentNullException.ThrowIfNull(workspaceToolExecutor);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(loopDefinition);
        ArgumentNullException.ThrowIfNull(toolResultRetentionStore);

        _paths = paths;
        _permissionService = permissionService;
        _approvalPrompt = approvalPrompt;
        _workspaceToolExecutor = workspaceToolExecutor;
        _auditLog = auditLog;
        _loopDefinition = loopDefinition;
        _toolResultRetention = new ToolResultRetentionService(auditLog, loopDefinition, toolResultRetentionStore);
        _governanceObserver = governanceObserver;
        _actuationAuthorityRevalidator = actuationAuthorityRevalidator;
        AvailableCommands = GetAvailableCommands(_loopDefinition);
        _auditMetadataFactory = new ToolAuditMetadataFactory(_paths, _loopDefinition, AvailableCommands);
        _postActuationIntegrityTimeout = postActuationIntegrityTimeout ?? _defaultPostActuationIntegrityTimeout;
        if (_postActuationIntegrityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(postActuationIntegrityTimeout), "Post-actuation integrity timeout must be positive.");
        }
    }

    /// <summary>
    /// Gets the commands admitted by the active loop's capabilities.
    /// </summary>
    /// <value>The available commands tool commands.</value>
    public IReadOnlyList<ToolCommand> AvailableCommands { get; }

    /// <summary>
    /// Governs and, when authorized, executes a workspace command.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The terminal result together with governance and retention evidence.</returns>
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = BoundCorrelation(request);
        var requestId = Guid.NewGuid().ToString("N");

        // Loop capability is the outer authority boundary. A directory policy cannot grant a command
        // that the active loop never admitted.
        if (!IsCommandAvailable(request.Command))
        {
            await RecordLoopAuthorityAsync(requestId, request, request.TargetPath, AuditSchema.Outcomes.Denied, cancellationToken);
            var detail = $"Active loop `{_loopDefinition.Id}` does not grant `{LoopCapabilityIds.WorkspaceCommandFor(request.Command)}` or `{LoopCapabilityIds.WorkspaceCommand}`.";
            var evidence = AuthorityDenied(detail);
            await ObserveDecisionAsync(requestId, request, request.TargetPath, evidence, cancellationToken);
            var result = new ToolResult(ToolExecutionOutcome.Denied, $"denied: {detail}", requestId, request.TargetPath, request, evidence);
            return await RetainAndObserveOutcomeAsync(result, cancellationToken);
        }

        var check = _permissionService.Evaluate(request);

        // Persist both capability and policy decisions before resolving the request so denied attempts
        // remain auditable even when no actuation follows.
        await RecordLoopAuthorityAsync(requestId, request, check.ResolvedPath, AuditSchema.Outcomes.Allowed, cancellationToken);
        await RecordPermissionAsync(requestId, request, check, cancellationToken);

        if (check.Evaluation.Decision == PermissionDecision.Deny)
        {
            var evidence = DecisionEvidence(check, ToolApprovalDecision.NotEvaluated, null);
            return await FinalizeTerminalOutcomeAsync(requestId, request, check, false, new ToolTerminalOutcome(ToolExecutionOutcome.Denied, $"denied: {check.Evaluation.Detail}", evidence, AuditSchema.Outcomes.Denied, new Dictionary<string, object?>()), cancellationToken);
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
                return await FinalizeTerminalOutcomeAsync(requestId, request, check, false, new ToolTerminalOutcome(ToolExecutionOutcome.ApprovalRejected, $"rejected: {approvalResponse.Detail}", evidence, AuditSchema.Outcomes.ApprovalRejected, new Dictionary<string, object?>()), cancellationToken);
            }

            approvedByHuman = true;
        }

        var approvalDecision = approvedByHuman ? ToolApprovalDecision.Approved : ToolApprovalDecision.NotRequired;
        if (_actuationAuthorityRevalidator is not null)
        {
            // Approval is evidence for one decision, not permanent authority. Revalidate mutable authority
            // immediately before touching the workspace to close the time-of-check/time-of-use window.
            var revalidation = await _actuationAuthorityRevalidator.RevalidateAsync(request, cancellationToken);
            ArgumentNullException.ThrowIfNull(revalidation);
            ArgumentException.ThrowIfNullOrWhiteSpace(revalidation.Detail);
            ArgumentNullException.ThrowIfNull(revalidation.AuditMetadata);
            await RecordActuationAuthorityAsync(requestId, request, check, revalidation, cancellationToken);
            if (!revalidation.Allowed)
            {
                var evidence = RevalidationDeniedEvidence(check, approvalDecision, approvalResponse, revalidation.Detail);
                return await FinalizeTerminalOutcomeAsync(requestId, request, check, approvedByHuman, new ToolTerminalOutcome(ToolExecutionOutcome.Denied, $"denied: {revalidation.Detail}", evidence, AuditSchema.Outcomes.Denied, revalidation.AuditMetadata), cancellationToken);
            }
        }

        var authorizedEvidence = DecisionEvidence(check, approvalDecision, approvalResponse);
        await RecordExecutionIntentAsync(requestId, request, check, approvedByHuman, cancellationToken);
        await ObserveDecisionAsync(requestId, request, check.ResolvedPath, authorizedEvidence, cancellationToken);
        return await ExecuteAuthorizedAsync(requestId, request, check, approvedByHuman, authorizedEvidence, cancellationToken);
    }

    private async Task<ToolResult> ExecuteAuthorizedAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, ToolGovernanceEvidence governance, CancellationToken cancellationToken)
    {
        ToolResult result;
        IReadOnlyDictionary<string, object?> executionMetadata;
        string executionOutcome;
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

            result = new ToolResult(ToolExecutionOutcome.Succeeded, output.Text, requestId, check.ResolvedPath, request, governance);
            executionMetadata = output.Metadata;
            executionOutcome = AuditSchema.Outcomes.Succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            result = new ToolResult(ToolExecutionOutcome.Failed, $"failed: {exception.Message}", requestId, check.ResolvedPath, request, governance);
            executionMetadata = ToolAuditMetadataFactory.ForError(exception);
            executionOutcome = AuditSchema.Outcomes.Failed;
        }

        // The workspace operation has already completed. Finish retention, observation, and audit with a
        // bounded independent token so caller cancellation cannot encourage an unsafe retry with a new id.
        using var integrity = new CancellationTokenSource(_postActuationIntegrityTimeout);
        try
        {
            result = await _toolResultRetention.RetainAsync(result, cancellationToken: integrity.Token);
        }
        catch (Exception exception)
        {
            result = WithPostActuationIntegrityWarning(result, "full-response retention", exception);
        }

        try
        {
            await ObserveOutcomeAsync(result, integrity.Token);
        }
        catch (Exception exception)
        {
            result = WithPostActuationIntegrityWarning(result, "outcome observation", exception);
        }

        try
        {
            await RecordExecutionAsync(requestId, request, check, approvedByHuman, executionOutcome, executionMetadata, integrity.Token);
        }
        catch (Exception exception)
        {
            result = WithPostActuationIntegrityWarning(result, "execution audit", exception);
        }

        return result;
    }

    private async Task<ToolResult> FinalizeTerminalOutcomeAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, ToolTerminalOutcome outcome, CancellationToken cancellationToken)
    {
        await ObserveDecisionAsync(requestId, request, check.ResolvedPath, outcome.GovernanceEvidence, cancellationToken);
        var result = new ToolResult(outcome.Outcome, outcome.Detail, requestId, check.ResolvedPath, request, outcome.GovernanceEvidence);
        result = await RetainAndObserveOutcomeAsync(result, cancellationToken);
        await RecordExecutionAsync(requestId, request, check, approvedByHuman, outcome.AuditOutcome, outcome.AuditMetadata, cancellationToken);
        return result;
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

    private async Task<ToolResult> RetainAndObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken)
    {
        result = await _toolResultRetention.RetainAsync(result, cancellationToken: cancellationToken);
        await ObserveOutcomeAsync(result, cancellationToken);
        return result;
    }

    private static ToolGovernanceEvidence AuthorityDenied(string detail)
    {
        return new ToolGovernanceEvidence(ToolAuthorityDecision.Denied, detail, null, null, null, null, ToolApprovalDecision.NotEvaluated, null, null);
    }

    private static ToolRequest BoundCorrelation(ToolRequest request)
    {
        if (request.CorrelationId is null || request.CorrelationId.Length <= MaxCorrelationCharacters)
        {
            return request;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.CorrelationId))).ToLowerInvariant();
        return request with { CorrelationId = $"sha256:{hash}" };
    }

    private static ToolResult WithPostActuationIntegrityWarning(ToolResult result, string phase, Exception exception)
    {
        var disposition = exception is OperationCanceledException ? "timed out" : "failed";
        var warning = $"Post-actuation {phase} {disposition} ({exception.GetType().Name}). The workspace operation already finished, so this result must not be retried under a new operation id; inspect the workspace before any follow-up mutation.";
        var retention = result.Retention ?? new ToolResultRetentionReference(
            ToolResultRetentionStatus.Unavailable,
            null,
            null,
            result.OutputText.Length,
            null,
            null,
            null,
            0,
            "Durable full-response retention did not produce a reference.");
        return result with { Retention = retention with { Detail = $"{retention.Detail} {warning}" } };
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

    private static ToolGovernanceEvidence RevalidationDeniedEvidence(ToolPermissionCheck check, ToolApprovalDecision approvalDecision, ToolApprovalResponse? approval, string detail)
    {
        return new ToolGovernanceEvidence(
            ToolAuthorityDecision.Denied,
            detail,
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

    private Task RecordActuationAuthorityAsync(string requestId, ToolRequest request, ToolPermissionCheck check, ToolActuationAuthorityRevalidation revalidation, CancellationToken cancellationToken)
    {
        var metadata = _auditMetadataFactory.CreateBase(requestId, request, check);
        metadata["authority_phase"] = "pre_actuation_revalidation";
        foreach (var item in revalidation.AuditMetadata)
        {
            metadata[item.Key] = item.Value;
        }

        return AppendAuditAsync(AuditEvent.Create(
            AuditSchema.Actors.Tool,
            AuditSchema.Actions.ToolLoopAuthorityEvaluate,
            check.ResolvedPath,
            revalidation.Allowed ? AuditSchema.Outcomes.Allowed : AuditSchema.Outcomes.Denied,
            revalidation.Detail,
            metadata), cancellationToken);
    }

    private bool IsCommandAvailable(ToolCommand command)
    {
        return AvailableCommands.Contains(command);
    }

    private static IReadOnlyList<ToolCommand> GetAvailableCommands(LoopDefinition loopDefinition)
    {
        return _allCommands.Where(command => LoopCapabilityIds.AllowsWorkspaceCommand(loopDefinition.CapabilityIds, command)).ToArray();
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
