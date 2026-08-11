using System.Collections.ObjectModel;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Projects the canonical governed-loop effect boundary into the workspace-tool actuator protocol.</summary>
public sealed class GovernedLoopToolActuationAuthorityBoundary : IToolActuationAuthorityBoundary
{
    private readonly IGovernedLoopEffectAuthorityBoundary _effectAuthorityBoundary;
    private readonly GovernedLoopAdmissionReceipt _admissionReceipt;
    private readonly GovernedLoopExecutionBinding _executionBinding;
    private readonly GovernedLoopGraphRevisionArtifact _graphArtifact;
    private readonly string _nodeId;
    private readonly int _nodeAttempt;
    private readonly string _serverCorrelationId;

    /// <summary>Creates one attempt-local adapter over complete exact retained run evidence.</summary>
    /// <param name="effectAuthorityBoundary">The canonical durable effect-authority boundary.</param>
    /// <param name="admissionReceipt">The complete exact successful admission receipt retained by the run.</param>
    /// <param name="executionBinding">The exact run, revision, and execution generation.</param>
    /// <param name="graphArtifact">The exact immutable graph artifact retained by the run.</param>
    /// <param name="nodeId">The exact provider-Inference node identity.</param>
    /// <param name="nodeAttempt">The exact positive node-attempt number.</param>
    /// <param name="serverCorrelationId">The exact attempt-local server correlation identity.</param>
    public GovernedLoopToolActuationAuthorityBoundary(
        IGovernedLoopEffectAuthorityBoundary effectAuthorityBoundary,
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        GovernedLoopGraphRevisionArtifact graphArtifact,
        string nodeId,
        int nodeAttempt,
        string serverCorrelationId)
    {
        _effectAuthorityBoundary = effectAuthorityBoundary ?? throw new ArgumentNullException(nameof(effectAuthorityBoundary));
        _admissionReceipt = admissionReceipt ?? throw new ArgumentNullException(nameof(admissionReceipt));
        _executionBinding = executionBinding ?? throw new ArgumentNullException(nameof(executionBinding));
        _graphArtifact = graphArtifact ?? throw new ArgumentNullException(nameof(graphArtifact));
        _nodeId = nodeId;
        _nodeAttempt = nodeAttempt;
        _serverCorrelationId = serverCorrelationId;
    }

    /// <inheritdoc />
    public async Task<ToolActuationAuthorityExecution> ExecuteAsync<TResult>(
        ToolRequest request,
        Func<ToolActuationAuthorityExecution, CancellationToken, Task<TResult>> executeActuatorAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executeActuatorAsync);
        var effectRequest = WorkspaceToolEffectAuthorityRequestFactory.Create(
            _admissionReceipt,
            _executionBinding,
            _graphArtifact,
            _nodeId,
            _nodeAttempt,
            _serverCorrelationId,
            request,
            GovernedLoopEffectBoundaryKind.WorkspaceActuation);
        var direct = new ToolActuationAuthorityExecution(
            ToolActuationAuthorityDisposition.Direct,
            "Durable governed-loop authority directly admitted the exact read-only workspace actuation.",
            CreateMetadata(
                effectRequest,
                GovernedLoopEffectAuthorityExecutionStatus.Decided,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                GovernedLoopEffectAuthorityDisposition.Direct,
                null));

        var callbackSync = new object();
        var callbackCount = 0;
        var callbackClosed = false;
        ToolActuationAuthorityProtocolException? callbackViolation = null;
        async Task<ToolActuationAuthorityExecution> CommitAsync(CancellationToken token)
        {
            lock (callbackSync)
            {
                callbackCount++;
                if (callbackClosed || callbackCount != 1)
                {
                    callbackViolation ??= Protocol("The governed effect boundary invoked the workspace actuator more than once or after returning.");
                    throw callbackViolation;
                }
            }

            try
            {
                _ = await executeActuatorAsync(direct, token).ConfigureAwait(false);
            }
            catch (ToolActuationAuthorityProtocolException exception)
            {
                lock (callbackSync)
                {
                    callbackViolation ??= exception;
                }

                throw;
            }

            return direct;
        }

        GovernedLoopEffectAuthorityExecutionResult<ToolActuationAuthorityExecution> result;
        try
        {
            result = await _effectAuthorityBoundary.ExecuteAsync(effectRequest, CommitAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (callbackSync)
            {
                callbackClosed = true;
            }
        }

        lock (callbackSync)
        {
            if (callbackViolation is not null)
            {
                throw callbackViolation;
            }
        }

        ValidateProtocolResult(effectRequest, result, callbackCount, direct);
        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided)
        {
            return result.Decision!.Disposition switch
            {
                GovernedLoopEffectAuthorityDisposition.Direct => direct,
                GovernedLoopEffectAuthorityDisposition.Deny => new ToolActuationAuthorityExecution(
                    ToolActuationAuthorityDisposition.Denied,
                    $"Current governed-loop authority denied the exact workspace actuation ({Token(result.Decision.Reason)}).",
                    CreateMetadata(effectRequest, result.Status, result.EvidenceStatus, result.Decision.Disposition, result.Decision)),
                GovernedLoopEffectAuthorityDisposition.Pause => new ToolActuationAuthorityExecution(
                    ToolActuationAuthorityDisposition.ReviewRequired,
                    $"Current governed-loop authority paused the exact workspace actuation for review ({Token(result.Decision.Reason)}).",
                    CreateMetadata(effectRequest, result.Status, result.EvidenceStatus, result.Decision.Disposition, result.Decision)),
                _ => throw Protocol("The governed effect boundary returned an unknown durable disposition."),
            };
        }

        return new ToolActuationAuthorityExecution(
            ToolActuationAuthorityDisposition.Ambiguous,
            $"Governed-loop authority could not prove a safe exact workspace actuation ({Token(result.Status)}/{Token(result.EvidenceStatus)}).",
            CreateMetadata(effectRequest, result.Status, result.EvidenceStatus, result.Decision?.Disposition, result.Decision));
    }

    private static void ValidateProtocolResult(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityExecutionResult<ToolActuationAuthorityExecution>? result,
        int callbackCount,
        ToolActuationAuthorityExecution direct)
    {
        if (result is null || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.EvidenceStatus))
        {
            throw Protocol("The governed effect boundary returned a malformed execution result.");
        }

        if (callbackCount is < 0 or > 1
            || result.CommitInvoked != (callbackCount == 1)
            || (!result.CommitInvoked && result.Result is not null))
        {
            throw Protocol("The governed effect boundary returned a result inconsistent with its actuator invocation count.");
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided)
        {
            if (result.Decision is null
                || result.EvidenceStatus is not (GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)
                || !IsExactDecision(request, result.Decision))
            {
                throw Protocol("A decided governed effect did not carry exact durable authority evidence for this workspace request.");
            }

            if (result.Decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct)
            {
                if (result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                    || callbackCount != 1
                    || !result.CommitInvoked
                    || !ReferenceEquals(result.Result, direct))
                {
                    throw Protocol("Direct governed tool authority did not return the same single-use decision instance supplied to the actuator.");
                }
            }
            else if (callbackCount != 0 || result.CommitInvoked || result.Result is not null)
            {
                throw Protocol("A deny or pause governed effect decision invoked the workspace actuator.");
            }

            return;
        }

        if (result.Status is GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest or GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable)
        {
            if (result.Decision is not null || result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown || callbackCount != 0)
            {
                throw Protocol("An unavailable or invalid governed effect result carried contradictory authority evidence.");
            }

            return;
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected)
        {
            if (callbackCount != 0 || result.CommitInvoked || result.Result is not null || result.Decision is not null && !IsExactDecision(request, result.Decision))
            {
                throw Protocol("An evidence-rejected governed effect result crossed or contradicted the workspace actuator boundary.");
            }

            return;
        }

        throw Protocol("The governed effect boundary returned an unsupported execution status.");
    }

    private static bool IsExactDecision(GovernedLoopEffectAuthorityRequest request, GovernedLoopEffectAuthorityDecision decision)
    {
        return GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid
            && string.Equals(decision.RunId, request.ExecutionBinding.RunId, StringComparison.Ordinal)
            && decision.ExecutionGeneration == request.ExecutionBinding.ExecutionGeneration
            && string.Equals(decision.NodeId, request.NodeId, StringComparison.Ordinal)
            && decision.NodeAttempt == request.NodeAttempt
            && string.Equals(decision.EffectOperationId, request.EffectOperationId, StringComparison.Ordinal)
            && string.Equals(decision.CorrelationId, request.CorrelationId, StringComparison.Ordinal)
            && decision.BoundaryKind == request.BoundaryKind
            && string.Equals(decision.AdmissionReceiptHash, request.AdmissionReceipt.ContentHash, StringComparison.Ordinal)
            && AuthorityCeilingSubset.IsEqual(decision.RequiredAuthority, request.RequiredAuthority)
            && decision.RequiredCapabilityPins.SequenceEqual(request.RequiredCapabilityPins);
    }

    private static IReadOnlyDictionary<string, object?> CreateMetadata(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityExecutionStatus executionStatus,
        GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus,
        GovernedLoopEffectAuthorityDisposition? disposition,
        GovernedLoopEffectAuthorityDecision? decision)
    {
        return new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["effect_authority_execution_status"] = Token(executionStatus),
            ["effect_authority_evidence_status"] = Token(evidenceStatus),
            ["effect_authority_disposition"] = disposition is null ? null : Token(disposition.Value),
            ["effect_authority_reason"] = decision is null ? null : Token(decision.Reason),
            ["effect_authority_decision_hash"] = decision?.ContentHash,
            ["effect_operation_id"] = request.EffectOperationId,
            ["effect_boundary_kind"] = Token(request.BoundaryKind),
            ["effect_correlation_id"] = request.CorrelationId,
            ["effect_run_id"] = request.ExecutionBinding.RunId,
            ["effect_execution_generation"] = request.ExecutionBinding.ExecutionGeneration,
            ["effect_node_id"] = request.NodeId,
            ["effect_node_attempt"] = request.NodeAttempt,
            ["effect_admission_receipt_hash"] = request.AdmissionReceipt.ContentHash,
            ["effect_graph_artifact_hash"] = request.GraphArtifact.ArtifactHash,
        });
    }

    private static string Token<TEnum>(TEnum value) where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    private static ToolActuationAuthorityProtocolException Protocol(string message) => new(message);
}
