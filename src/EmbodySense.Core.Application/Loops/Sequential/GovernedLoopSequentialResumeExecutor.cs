using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Routes legacy resumes unchanged and reconstructs canonical resumes only from exact immutable evidence.</summary>
public sealed class GovernedLoopSequentialResumeExecutor : ICustomLoopResumeExecutor
{
    private readonly ICustomLoopRunStore _runStore;
    private readonly IGovernedLoopSequentialRunEvidenceSource _runEvidenceSource;
    private readonly IGovernedLoopAdmissionStore _admissionStore;
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;
    private readonly ICustomLoopResumeExecutor _legacyExecutor;

    /// <summary>Creates the canonical-aware resume router over immutable admission and graph stores.</summary>
    public GovernedLoopSequentialResumeExecutor(
        ICustomLoopRunStore runStore,
        IGovernedLoopSequentialRunEvidenceSource runEvidenceSource,
        IGovernedLoopAdmissionStore admissionStore,
        IGovernedLoopGraphRevisionStore graphStore,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime,
        ICustomLoopResumeExecutor legacyExecutor)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _runEvidenceSource = runEvidenceSource ?? throw new ArgumentNullException(nameof(runEvidenceSource));
        _admissionStore = admissionStore ?? throw new ArgumentNullException(nameof(admissionStore));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
        _legacyExecutor = legacyExecutor ?? throw new ArgumentNullException(nameof(legacyExecutor));
    }

    /// <inheritdoc />
    public async Task<CustomLoopOrderedRunResult> ResumeAsync(
        CustomLoopResumeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await _runStore.GetAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Result(CustomLoopOrderedRunStatus.NotFound, null, "The custom-loop run does not exist.");
        }

        if (run.SequentialAdapterBinding is null && run.SequentialInvocationSnapshot is null)
        {
            return await _legacyExecutor.ResumeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (run.SequentialAdapterBinding is null
            || run.SequentialInvocationSnapshot is null
            || !GovernedLoopSequentialContractValidator.Validate(run.SequentialAdapterBinding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(run.SequentialInvocationSnapshot).IsValid)
        {
            return Invalid(run, "Canonical resume found an incomplete or invalid durable sequential hand-off.");
        }

        GovernedLoopSequentialRunEvidence? evidence;
        GovernedLoopAdmissionStoreReadResult admissionRead;
        try
        {
            evidence = await _runEvidenceSource.ResolveAsync(run.Id, cancellationToken).ConfigureAwait(false);
            admissionRead = await _admissionStore.ReadByOperationAsync(
                run.SequentialAdapterBinding.WorkspaceId,
                run.SequentialAdapterBinding.AdmissionOperationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.Failed, run, $"Canonical resume could not read immutable run or admission evidence: {exception.GetType().Name}.");
        }

        if (evidence is null
            || !string.Equals(evidence.AdapterBinding.ContentHash, run.SequentialAdapterBinding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(evidence.InvocationSnapshot.ContentHash, run.SequentialInvocationSnapshot.ContentHash, StringComparison.Ordinal))
        {
            return Invalid(run, "Canonical resume could not authenticate the exact persisted binding and invocation snapshot.");
        }

        var outcome = admissionRead.Status == GovernedLoopAdmissionStoreReadStatus.Found
            ? admissionRead.Outcome
            : null;
        if (outcome is null
            || !GovernedLoopAdmissionValidator.Validate(outcome).IsValid
            || outcome.Disposition != GovernedLoopAdmissionDisposition.Admitted
            || outcome.Receipt is null
            || outcome.Rejection is not null)
        {
            return Invalid(run, "Canonical resume requires one authenticated Found admission receipt; mutable, pending, rejected, unavailable, or ambiguous evidence is not executable.");
        }

        var binding = evidence.AdapterBinding;
        var invocation = evidence.InvocationSnapshot;
        var receipt = outcome.Receipt;
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
            return Invalid(run, "Canonical resume reconstructed a request or receipt identity that diverges from the durable adapter binding.");
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
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.Failed, run, $"Canonical resume could not read the pinned immutable graph artifact: {exception.GetType().Name}.");
        }

        if (artifactRead.Status != GovernedLoopRevisionStoreReadStatus.Ready
            || artifactRead.Artifact is not { } artifact
            || !string.Equals(artifact.ArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(artifact.LayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal))
        {
            return Invalid(run, "Canonical resume requires the exact immutable graph artifact pinned by the durable execution binding.");
        }

        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(
            binding,
            admissionRequest,
            receipt,
            invocation,
            artifact);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        if (anchorResult.Status != GovernedLoopSequentialRunAnchorStatus.Ready
            || anchorResult.Anchor is null
            || planResult.Status != GovernedLoopSequentialPlanBuildStatus.Ready
            || planResult.Plan is null)
        {
            return Invalid(run, "Canonical resume could not rebuild the exact admitted run anchor and deterministic plan.");
        }

        if (!GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, planResult.Plan))
        {
            return Invalid(run, "Canonical resume requires one hash-valid durable frontier matching the exact admitted plan; missing, stale, corrupt, or substituted progress is nondispatchable.");
        }

        return await _orderedRuntime.ResumeAsync(
            new GovernedLoopSequentialOrderedResumeRequest(
                GovernedLoopSequentialOrderedResumeRequest.CurrentSchemaVersion,
                anchorResult.Anchor,
                planResult.Plan,
                artifact,
                request.RunningLifecycleVersion,
                request.ResumeOperationId,
                request.Actor,
                request.ActiveRunAlreadyRegistered),
            cancellationToken).ConfigureAwait(false);
    }

    private static CustomLoopOrderedRunResult Invalid(CustomLoopRunRecord run, string detail)
        => Result(CustomLoopOrderedRunStatus.InvalidState, run, detail);

    private static CustomLoopOrderedRunResult Result(
        CustomLoopOrderedRunStatus status,
        CustomLoopRunRecord? run,
        string detail)
        => new(status, run, detail);
}
