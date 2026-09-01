using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution.Effects;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Performs one bounded, read-only dependency probe before Human Review becomes executable.</summary>
/// <remarks>
/// The probe verifies the canonical graph, lifecycle-projected capability admission, authority, effect, clock, and
/// decision-provider seams without creating a review, publishing a continuation, releasing an effect, or dispatching
/// work. Authority artifacts must be present and prove an exact empty lookup; absent or partially present state remains
/// fail-closed.
/// </remarks>
internal sealed class HumanReviewRuntimeDependencyReadinessProbe
{
    private readonly WorkspacePaths _paths;
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly ICapabilityAdmissionService _capabilityAdmission;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly GovernedLoopEffectAttemptComposition _effectAttempts;
    private readonly TimeProvider _trustedClock;
    private readonly IHumanReviewDecisionAuthorizationProvider? _decisionProvider;
    private readonly HumanReviewRuntimeCompositionReadiness _composition;

    internal HumanReviewRuntimeDependencyReadinessProbe(
        WorkspacePaths paths,
        IGovernedLoopGraphRevisionStore graphStore,
        ICapabilityAdmissionService capabilityAdmission,
        ICapabilityAuthorityTransaction authorityTransaction,
        IAuthorityGrantResolver grantResolver,
        GovernedLoopEffectAttemptComposition effectAttempts,
        TimeProvider trustedClock,
        IHumanReviewDecisionAuthorizationProvider? decisionProvider,
        HumanReviewRuntimeCompositionReadiness composition)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _capabilityAdmission = capabilityAdmission ?? throw new ArgumentNullException(nameof(capabilityAdmission));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _effectAttempts = effectAttempts ?? throw new ArgumentNullException(nameof(effectAttempts));
        _trustedClock = trustedClock ?? throw new ArgumentNullException(nameof(trustedClock));
        _decisionProvider = decisionProvider;
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
    }

    /// <summary>Reads every required dependency under bounded limits and returns a fail-closed executable posture.</summary>
    /// <param name="cancellationToken">The caller token propagated to every dependency read.</param>
    /// <returns><see langword="true"/> only when every dependency provides a current, structurally valid posture.</returns>
    internal async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_composition.IsComposed || _decisionProvider is null || !IsHealthyTrustedClock())
            {
                return false;
            }

            if (!await ProbeAuthorityTransactionAsync(cancellationToken).ConfigureAwait(false)
                || !await ProbeGraphStoreAsync(cancellationToken).ConfigureAwait(false)
                || !await ProbeCapabilityAdmissionAsync(cancellationToken).ConfigureAwait(false)
                || !await ProbeGrantResolverAsync(cancellationToken).ConfigureAwait(false)
                || !await _effectAttempts.IsHumanReviewEvidenceStorageHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ProbeAuthorityTransactionAsync(CancellationToken cancellationToken)
    {
        var result = await _authorityTransaction.ExecuteAsync(
            static _ => Task.FromResult(true),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<bool> ProbeGraphStoreAsync(CancellationToken cancellationToken)
    {
        var graphId = "human-review-readiness-" + Guid.NewGuid().ToString("N");
        var read = await _graphStore.ReadGraphAsync(graphId, cancellationToken).ConfigureAwait(false);
        if (read is null || read.StoreGeneration < 0 || !Enum.IsDefined(read.Status))
        {
            return false;
        }

        return read.Status switch
        {
            EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreReadStatus.NotFound => read.Snapshot is null,
            EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreReadStatus.Ready => IsBoundedGraphSnapshot(read.Snapshot),
            _ => false
        };
    }

    private static bool IsBoundedGraphSnapshot(EmbodySense.Core.Application.Loops.GraphAuthoring.Models.GovernedLoopGraphRevisionSnapshot? snapshot)
    {
        return snapshot?.Lifecycle is { Head: not null, Artifacts: not null, Operations: not null }
            && snapshot.Lifecycle.Artifacts.Count <= GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph
            && snapshot.Lifecycle.Operations.Count <= GovernedLoopRevisionContractLimits.MaxOperationsPerGraph
            && snapshot.Artifacts is not null
            && snapshot.Artifacts.Count <= GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph
            && snapshot.Artifacts.All(artifact => artifact is not null)
            && snapshot.Lifecycle.Operations.All(operation => operation is not null);
    }

    private async Task<bool> ProbeCapabilityAdmissionAsync(CancellationToken cancellationToken)
    {
        if (!CapabilityId.TryParse("org.embodysense/human-review-readiness", out var subjectId, out _))
        {
            return false;
        }

        var requirements = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subjectId!,
            [],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
        var admission = await _capabilityAdmission.AdmitAsync(requirements, [], cancellationToken).ConfigureAwait(false);
        if (admission is null
            || !admission.IsAdmitted
            || admission.Snapshot is null
            || admission.Snapshot.SchemaVersion != CapabilityAdmissionSnapshot.CurrentSchemaVersion
            || admission.Snapshot.Pins is null
            || admission.Snapshot.Pins.Count != 0
            || admission.Snapshot.Evidence is null
            || admission.Snapshot.Evidence.Count != 0)
        {
            return false;
        }

        var revalidation = await _capabilityAdmission.RevalidateAsync(admission.Snapshot, [], cancellationToken).ConfigureAwait(false);
        return revalidation is not null
            && revalidation.Status == CapabilityRevalidationStatus.Active
            && revalidation.IsValid
            && revalidation.EffectivePins is not null
            && revalidation.EffectivePins.Count == 0
            && revalidation.ObservedPins is not null
            && revalidation.ObservedPins.Count == 0;
    }

    private async Task<bool> ProbeGrantResolverAsync(CancellationToken cancellationToken)
    {
        var grantId = AuthorityGrantId.TryParse("human-review-readiness-" + Guid.NewGuid().ToString("N"), out var parsedId, out _)
            ? parsedId
            : null;
        var revision = AuthorityGrantRevision.TryParse("1", out var parsedRevision, out _) ? parsedRevision : null;
        if (grantId is null || revision is null)
        {
            return false;
        }

        var reference = new AuthorityGrantReference(grantId, revision, "sha256:" + new string('0', 64));
        var read = await _grantResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        return File.Exists(_paths.AuthorityProfilesDocumentPath)
            && File.Exists(_paths.AuthorityProfilesProofPath)
            && read is not null
            && read.Status == AuthorityGrantResolutionStatus.NotFound
            && read.Grant is null
            && read.CurrentGrant is null;
    }

    private bool IsHealthyTrustedClock()
    {
        var now = _trustedClock.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var timestamp = _trustedClock.GetTimestamp();
        return _trustedClock.GetElapsedTime(timestamp, timestamp) >= TimeSpan.Zero;
    }
}
