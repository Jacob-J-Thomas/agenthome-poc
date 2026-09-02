using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.E2ETests.Web;

internal static class HumanReviewResponseLossOfflineProbe
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<HumanReviewResponseLossOfflineProbeResult> CaptureAsync(WorkspacePaths paths, string capabilityTrustRoot, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);
        return await CaptureCoreAsync(paths, capabilityTrustRoot, runId, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<HumanReviewResponseLossOfflineProbeResult> CaptureCoreAsync(WorkspacePaths paths, string capabilityTrustRoot, string runId, CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        using var runStore = new CustomLoopRunStore(paths);
        try
        {
            run = await runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            return Failure("corrupt", exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("unavailable", new TimeoutException("The bounded run read was cancelled."));
        }
        catch (Exception exception)
        {
            return Failure("unavailable", exception);
        }

        if (run is null)
        {
            return new HumanReviewResponseLossOfflineProbeResult("missing", null, null, null, null, null, null, null, null, null, null, null, null, null);
        }

        if (run.SequentialAdapterBinding is not { } adapter || run.HumanReview is not { } review || review.Request.Binding.EffectAttempt is not { } reviewed)
        {
            return new HumanReviewResponseLossOfflineProbeResult("found-corrupt", run, null, null, null, null, null, null, null, "The run did not retain the exact adapter, review, and effect binding.", null, null, null, null);
        }

        var authority = await CaptureAuthorityAsync(paths, capabilityTrustRoot, adapter, review, cancellationToken).ConfigureAwait(false);
        var effect = await CaptureEffectAsync(paths, runStore, adapter, review, reviewed, cancellationToken).ConfigureAwait(false);
        return new HumanReviewResponseLossOfflineProbeResult(
            "found",
            run,
            authority.AuthorityStatus,
            authority.GrantStatus,
            authority.CapabilityStatus,
            effect.EvidenceStatus,
            effect.CertaintyStatus,
            effect.AttemptStatus,
            effect.EffectPhase,
            null,
            authority.Error,
            effect.EvidenceError,
            effect.CertaintyError,
            effect.AttemptError);
    }

    private static async Task<(HumanReviewContinuationAuthorityReadStatus? AuthorityStatus, AuthorityGrantResolutionStatus? GrantStatus, CapabilityRevalidationStatus? CapabilityStatus, string? Error)> CaptureAuthorityAsync(
        WorkspacePaths paths,
        string capabilityTrustRoot,
        GovernedLoopSequentialAdapterBinding adapter,
        HumanReviewRunState review,
        CancellationToken cancellationToken)
    {
        try
        {
            var transaction = new CapabilityAuthorityTransaction(paths);
            var trust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
            var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
            var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
            var artifactRead = await graphStore.ReadArtifactAsync(adapter.ExecutionBinding.Revision, cancellationToken).ConfigureAwait(false);
            var artifact = artifactRead.Artifact;
            if (artifact is null)
            {
                return (HumanReviewContinuationAuthorityReadStatus.Unavailable, null, null, $"graph-artifact={artifactRead.Status}");
            }

            using var roleStore = new ContextualRoleRevisionStore(paths, adapter.WorkspaceId, authorityTransaction: transaction);
            var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
            var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
            var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
            var roleSource = new AuthorityGrantRoleSource(adapter.WorkspaceId, roleStore, roleStore, new WorkspaceContextualRoleInstructionSourceProbe(paths), transaction);
            var resolver = new HumanReviewResponseLossRecordingGrantResolver(new AuthorityGrantResolver(authorityStore, new AuthorityGrantProfileSource(authorityStore), roleSource, publicationSource, bindingSource, transaction));
            var capabilities = new HumanReviewResponseLossRecordingCapabilityAdmissionService(CapabilityAdmissionFactory.Create(paths, trust, transaction));
            var source = new CurrentHumanReviewContinuationAuthoritySource(resolver, capabilities);
            var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(review.Request.Binding, adapter, artifact), cancellationToken).ConfigureAwait(false);
            return (result.Status, resolver.Resolution?.Status, capabilities.Revalidation?.Status, resolver.ExceptionType is null && capabilities.ExceptionType is null ? null : $"grant-error={resolver.ExceptionType ?? "none"}; capability-error={capabilities.ExceptionType ?? "none"}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (HumanReviewContinuationAuthorityReadStatus.Unavailable, null, null, "authority=timeout");
        }
        catch (Exception exception)
        {
            return (HumanReviewContinuationAuthorityReadStatus.Unavailable, null, null, $"authority-error={exception.GetType().Name}");
        }
    }

    private static async Task<(HumanReviewCurrentEffectAttemptEvidenceReadStatus? EvidenceStatus, GovernedLoopEffectCertaintySnapshotStatus? CertaintyStatus, GovernedLoopEffectAttemptReadStatus? AttemptStatus, GovernedLoopEffectPhase? EffectPhase, string? EvidenceError, string? CertaintyError, string? AttemptError)> CaptureEffectAsync(
        WorkspacePaths paths,
        CustomLoopRunStore runStore,
        GovernedLoopSequentialAdapterBinding adapter,
        HumanReviewRunState review,
        HumanReviewEffectAttemptBinding reviewed,
        CancellationToken cancellationToken)
    {
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var attemptStatus = (GovernedLoopEffectAttemptReadStatus?)null;
        GovernedLoopEffectAttempt? attempt = null;
        string? attemptError = null;
        try
        {
            var direct = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(workspaceId, reviewed.OperationId, reviewed.EffectGeneration, cancellationToken).ConfigureAwait(false);
            attemptStatus = direct.Status;
            attempt = direct.Attempt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            attemptStatus = GovernedLoopEffectAttemptReadStatus.Unavailable;
            attemptError = "TimeoutException";
        }
        catch (Exception exception)
        {
            attemptStatus = GovernedLoopEffectAttemptReadStatus.Unavailable;
            attemptError = exception.GetType().Name;
        }

        var evidenceStatus = (HumanReviewCurrentEffectAttemptEvidenceReadStatus?)null;
        HumanReviewCurrentEffectAttemptEvidence? evidence = null;
        string? evidenceError = null;
        try
        {
            var source = new CanonicalHumanReviewEffectEvidenceSource(runStore, new GovernedLoopEffectAttemptStore(paths));
            var result = await source.ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(review.Request.Binding, reviewed), cancellationToken).ConfigureAwait(false);
            evidenceStatus = result.Status;
            evidence = result.Evidence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidenceStatus = HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable;
            evidenceError = "TimeoutException";
        }
        catch (Exception exception)
        {
            evidenceStatus = HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable;
            evidenceError = exception.GetType().Name;
        }

        var certaintyStatus = (GovernedLoopEffectCertaintySnapshotStatus?)null;
        string? certaintyError = null;
        if (evidence is { } current)
        {
            try
            {
                var source = new CanonicalHumanReviewEffectEvidenceSource(runStore, new GovernedLoopEffectAttemptStore(paths));
                var certainty = await source.ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(current.Identity, current.Preparation), cancellationToken).ConfigureAwait(false);
                certaintyStatus = certainty.Status;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                certaintyStatus = GovernedLoopEffectCertaintySnapshotStatus.Unavailable;
                certaintyError = "TimeoutException";
            }
            catch (Exception exception)
            {
                certaintyStatus = GovernedLoopEffectCertaintySnapshotStatus.Unavailable;
                certaintyError = exception.GetType().Name;
            }
        }
        else if (attempt is not null)
        {
            try
            {
                var identity = HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, attempt);
                var preparation = HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, attempt);
                var source = new CanonicalHumanReviewEffectEvidenceSource(runStore, new GovernedLoopEffectAttemptStore(paths));
                var certainty = await source.ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation), cancellationToken).ConfigureAwait(false);
                certaintyStatus = certainty.Status;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                certaintyStatus = GovernedLoopEffectCertaintySnapshotStatus.Unavailable;
                certaintyError = "TimeoutException";
            }
            catch (Exception exception)
            {
                certaintyStatus = GovernedLoopEffectCertaintySnapshotStatus.Unavailable;
                certaintyError = exception.GetType().Name;
            }
        }

        return (evidenceStatus, certaintyStatus, attemptStatus, attempt?.Payload.Phase, evidenceError, certaintyError, attemptError);
    }

    private static HumanReviewResponseLossOfflineProbeResult Failure(string runReadStatus, Exception exception)
        => new(runReadStatus, null, null, null, null, null, null, null, null, exception.GetType().Name, null, null, null, null);
}
