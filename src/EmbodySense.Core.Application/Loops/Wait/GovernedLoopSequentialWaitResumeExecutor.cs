using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Reconstructs Wait continuation only from the run's immutable admission and revision pins.</summary>
public sealed class GovernedLoopSequentialWaitResumeExecutor : IGovernedLoopWaitOrderedResumePort
{
    private readonly IGovernedLoopSequentialRunEvidenceSource _runEvidenceSource;
    private readonly IGovernedLoopAdmissionStore _admissionStore;
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;

    /// <summary>Creates the canonical Wait re-entry boundary.</summary>
    public GovernedLoopSequentialWaitResumeExecutor(
        IGovernedLoopSequentialRunEvidenceSource runEvidenceSource,
        IGovernedLoopAdmissionStore admissionStore,
        IGovernedLoopGraphRevisionStore graphStore,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime)
    {
        _runEvidenceSource = runEvidenceSource ?? throw new ArgumentNullException(nameof(runEvidenceSource));
        _admissionStore = admissionStore ?? throw new ArgumentNullException(nameof(admissionStore));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWaitOrderedContext?> ResolveAsync(
        CustomLoopRunRecord run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SequentialAdapterBinding is not { } durableBinding
            || run.SequentialInvocationSnapshot is not { } durableInvocation
            || !GovernedLoopSequentialContractValidator.Validate(durableBinding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(durableInvocation).IsValid)
        {
            return null;
        }

        GovernedLoopSequentialRunEvidence? evidence;
        GovernedLoopAdmissionStoreReadResult admissionRead;
        try
        {
            evidence = await _runEvidenceSource.ResolveAsync(run.Id, cancellationToken).ConfigureAwait(false);
            admissionRead = await _admissionStore.ReadByOperationAsync(
                durableBinding.WorkspaceId,
                durableBinding.AdmissionOperationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (evidence is null
            || !string.Equals(evidence.AdapterBinding.ContentHash, durableBinding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(evidence.InvocationSnapshot.ContentHash, durableInvocation.ContentHash, StringComparison.Ordinal))
        {
            return null;
        }

        var outcome = admissionRead.Status == GovernedLoopAdmissionStoreReadStatus.Found
            ? admissionRead.Outcome
            : null;
        if (outcome is null
            || !GovernedLoopAdmissionValidator.Validate(outcome).IsValid
            || outcome.Disposition != GovernedLoopAdmissionDisposition.Admitted
            || outcome.Receipt is not { } receipt
            || outcome.Rejection is not null)
        {
            return null;
        }

        var binding = evidence.AdapterBinding;
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            receipt.Intent.OperationId,
            binding.InvocationPayloadHash,
            string.Empty,
            receipt.Intent.Publication,
            receipt.Intent.AuthorityGrant,
            receipt.Intent.ActorId,
            receipt.Intent.Surface));
        if (!string.Equals(admissionRequest.RequestHash, binding.AdmissionRequestHash, StringComparison.Ordinal)
            || !string.Equals(admissionRequest.RequestHash, receipt.Intent.RequestHash, StringComparison.Ordinal)
            || !string.Equals(receipt.ContentHash, binding.AdmissionReceiptHash, StringComparison.Ordinal))
        {
            return null;
        }

        GovernedLoopGraphRevisionArtifactReadResult artifactRead;
        try
        {
            artifactRead = await _graphStore.ReadArtifactAsync(binding.ExecutionBinding.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (artifactRead.Status != GovernedLoopRevisionStoreReadStatus.Ready
            || artifactRead.Artifact is not { } artifact
            || !string.Equals(artifact.ArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(artifact.LayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal))
        {
            return null;
        }

        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(
            binding,
            admissionRequest,
            receipt,
            evidence.InvocationSnapshot,
            artifact);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        if (anchorResult.Status != GovernedLoopSequentialRunAnchorStatus.Ready
            || anchorResult.Anchor is null
            || planResult.Status != GovernedLoopSequentialPlanBuildStatus.Ready
            || planResult.Plan is null
            || !GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, planResult.Plan))
        {
            return null;
        }

        return new GovernedLoopWaitOrderedContext(anchorResult.Anchor, planResult.Plan, artifact);
    }

    /// <inheritdoc />
    public Task<CustomLoopOrderedRunResult> ResumeAsync(
        GovernedLoopWaitOrderedResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        return _orderedRuntime.ResumeWaitAsync(
            new GovernedLoopSequentialOrderedWaitResumeRequest(
                GovernedLoopSequentialOrderedWaitResumeRequest.CurrentSchemaVersion,
                request.Context.Anchor,
                request.Context.Plan,
                request.Context.Artifact,
                request.ActivationOrdinal,
                request.ContinuationEvidenceHash,
                request.Actor),
            cancellationToken);
    }
}
