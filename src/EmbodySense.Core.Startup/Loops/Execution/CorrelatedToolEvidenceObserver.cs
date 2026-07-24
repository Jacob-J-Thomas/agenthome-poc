using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CorrelatedToolEvidenceObserver : IToolGovernanceObserver
{
    private readonly ICustomLoopToolEvidenceSink _sink;
    private readonly CustomLoopInferenceAttemptRequest _attempt;
    private readonly Dictionary<string, RequestEvidenceState> _requests = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public CorrelatedToolEvidenceObserver(ICustomLoopToolEvidenceSink sink, CustomLoopInferenceAttemptRequest attempt)
    {
        _sink = sink;
        _attempt = attempt;
    }

    public async Task ReserveAsync(ToolRequest request, string resolvedTarget, CustomLoopToolAuthoritySnapshot authority, int requestOrdinal, CancellationToken cancellationToken)
    {
        var correlationId = request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("A bounded tool request must have a correlation id before evidence reservation.");
        lock (_gate)
        {
            if (!_requests.TryAdd(correlationId, new RequestEvidenceState(requestOrdinal, request, resolvedTarget, authority)))
            {
                throw new CustomLoopToolEvidenceIntegrityException("A tool request correlation id was reused within one inference attempt.");
            }
        }

        await RecordAsync(State(correlationId), CustomLoopToolEvidencePhase.RequestReserved, null, null, null, false, cancellationToken);
    }

    public Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
    {
        return RecordAsync(State(request), CustomLoopToolEvidencePhase.GovernanceDecided, requestId, evidence, null, false, cancellationToken);
    }

    public Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken = default)
    {
        return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, false, cancellationToken);
    }

    public Task RecordReturnedAsync(ToolResult result, CancellationToken cancellationToken)
    {
        return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, true, cancellationToken);
    }

    public Task RecordIntegrityAsync(
        ToolRequest request,
        string resolvedTarget,
        CustomLoopToolAuthoritySnapshot authority,
        int requestOrdinal,
        CancellationToken cancellationToken)
    {
        _ = request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("A bounded repeated tool request must have a correlation id before its integrity evidence is retained.");
        return RecordAsync(
            new RequestEvidenceState(requestOrdinal, request, resolvedTarget, authority),
            CustomLoopToolEvidencePhase.IntegrityFailed,
            null,
            null,
            null,
            false,
            cancellationToken);
    }

    private async Task RecordAsync(RequestEvidenceState state, CustomLoopToolEvidencePhase phase, string? brokerRequestId, ToolGovernanceEvidence? governance, ToolResult? result, bool returnedToModel, CancellationToken cancellationToken)
    {
        var canonical = result is not null ? ToolResultFormatter.FormatResults([result]) : null;
        var evidence = new CustomLoopToolTraceEvidence(
            phase,
            state.Ordinal,
            state.Request.CorrelationId!,
            brokerRequestId,
            state.Request.Command,
            state.Request.TargetPath,
            state.Request.Content,
            state.Request.Pattern,
            state.ResolvedTarget,
            state.Authority,
            BoundGovernance(governance),
            result?.Outcome,
            canonical,
            canonical is null ? null : CustomLoopTraceContentHash.Compute(canonical),
            canonical?.Length,
            returnedToModel,
            CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
        await _sink.RecordAsync(_attempt.RunId, _attempt.Iteration, _attempt.StepId, _attempt.Attempt, evidence, cancellationToken);
    }

    private static ToolGovernanceEvidence? BoundGovernance(ToolGovernanceEvidence? governance)
    {
        if (governance is null)
        {
            return null;
        }

        ValidateGovernanceText(governance.AuthorityDetail, nameof(governance.AuthorityDetail), required: true);
        ValidateGovernanceText(governance.PermissionMatchedPath, nameof(governance.PermissionMatchedPath), required: false, CustomLoopLimits.MaxGovernedToolTargetCharacters);
        ValidateGovernanceText(governance.PermissionDetail, nameof(governance.PermissionDetail), required: false);
        ValidateGovernanceText(governance.ApprovalDecisionBy, nameof(governance.ApprovalDecisionBy), required: false);
        ValidateGovernanceText(governance.ApprovalDetail, nameof(governance.ApprovalDetail), required: false);
        return governance;
    }

    private static void ValidateGovernanceText(string? value, string field, bool required, int maximumCharacters = CustomLoopLimits.MaxToolGovernanceDetailCharacters)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value is not null && value.Length > maximumCharacters)
        {
            throw new CustomLoopToolEvidenceIntegrityException($"Governance field `{field}` exceeds its exact durable evidence bound.");
        }
    }

    private RequestEvidenceState State(ToolRequest request)
    {
        return State(request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("Governed tool evidence lost its request correlation id."));
    }

    private RequestEvidenceState State(string correlationId)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(correlationId, out var state)
                ? state
                : throw new CustomLoopToolEvidenceIntegrityException("Governed tool evidence was observed before its exact request reservation.");
        }
    }

    private sealed record RequestEvidenceState(int Ordinal, ToolRequest Request, string ResolvedTarget, CustomLoopToolAuthoritySnapshot Authority);
}
