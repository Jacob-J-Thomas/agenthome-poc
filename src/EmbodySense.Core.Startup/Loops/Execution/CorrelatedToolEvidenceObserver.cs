using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
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

    /// <summary>
    /// Creates an attempt-scoped observer that enforces one ordered evidence lifecycle per correlation identity.
    /// </summary>
    /// <param name="sink">The sink.</param>
    /// <param name="attempt">The attempt.</param>
    public CorrelatedToolEvidenceObserver(ICustomLoopToolEvidenceSink sink, CustomLoopInferenceAttemptRequest attempt)
    {
        _sink = sink;
        _attempt = attempt;
    }

    /// <summary>
    /// Reserves the exact bounded request, resolved target, ordinal, and authority before governance begins.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="resolvedTarget">The resolved target.</param>
    /// <param name="authority">The authority.</param>
    /// <param name="requestOrdinal">The request ordinal.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary>
    /// Accepts the generic broker approval notification; the later governance-decision evidence is
    /// the canonical custom-loop phase retained by this observer.
    /// </summary>
    /// <param name="requestId">The request identifier.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the correlated governance decision after request reservation.
    /// </summary>
    /// <param name="requestId">The request identifier.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
    {
        return RecordAsync(State(request), CustomLoopToolEvidencePhase.GovernanceDecided, requestId, evidence, null, false, cancellationToken);
    }

    /// <summary>
    /// Records the canonical correlated tool outcome before it is returned to the model.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken = default)
    {
        return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, false, cancellationToken);
    }

    /// <summary>
    /// Records that the canonical correlated result was returned to the model.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task RecordReturnedAsync(ToolResult result, CancellationToken cancellationToken)
    {
        return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, true, cancellationToken);
    }

    /// <summary>
    /// Replaces the reserved request's authority with its actuation-boundary revalidation snapshot.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="authority">The authority.</param>
    public void RefreshAuthority(ToolRequest request, CustomLoopToolAuthoritySnapshot authority)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);
        var correlationId = request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("Governed tool authority refresh lost its request correlation id.");
        lock (_gate)
        {
            var state = _requests.TryGetValue(correlationId, out var reserved)
                ? reserved
                : throw new CustomLoopToolEvidenceIntegrityException("Governed tool authority was refreshed before its exact request reservation.");
            _requests[correlationId] = state with { Authority = authority };
        }
    }

    /// <summary>
    /// Records bounded non-actuating evidence for a repeated request that violates the tool evidence contract.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="resolvedTarget">The resolved target.</param>
    /// <param name="authority">The authority.</param>
    /// <param name="requestOrdinal">The request ordinal.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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
            phase == CustomLoopToolEvidencePhase.IntegrityFailed
                ? CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes
                : CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
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
