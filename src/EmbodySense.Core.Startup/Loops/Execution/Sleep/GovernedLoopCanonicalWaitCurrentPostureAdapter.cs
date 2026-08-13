using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Projects the canonical durable run and exact current grant into the shared sleep posture contract.</summary>
internal sealed class GovernedLoopCanonicalWaitCurrentPostureAdapter : IGovernedLoopSleepCurrentPosturePort
{
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICustomLoopRunStore _runStore;
    private readonly TimeProvider _timeProvider;

    internal GovernedLoopCanonicalWaitCurrentPostureAdapter(
        ICustomLoopRunStore runStore,
        IAuthorityGrantResolver grantResolver,
        TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
        GovernedLoopExecutionBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GovernedLoopExecutionValidator.Validate(binding).IsValid)
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Conflict);
        }

        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetAsync(binding.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Unavailable);
        }

        if (run is null)
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.NotFound);
        }

        if (!CustomLoopRunValidator.Validate(run).IsValid
            || run.SequentialAdapterBinding is not { } adapterBinding
            || run.Frontier is null
            || !Equals(adapterBinding.ExecutionBinding, binding))
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Conflict);
        }

        var authorityReference = adapterBinding.AdmissionReceipt.Intent.AuthorityGrant;
        AuthorityGrantResolution resolution;
        try
        {
            resolution = await _grantResolver.ResolveAsync(authorityReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Unavailable);
        }

        if (resolution is null
            || resolution.Status is AuthorityGrantResolutionStatus.Unknown or AuthorityGrantResolutionStatus.Ambiguous or AuthorityGrantResolutionStatus.Invalid
            || resolution.Status == AuthorityGrantResolutionStatus.Unavailable)
        {
            return Result(resolution?.Status == AuthorityGrantResolutionStatus.Unavailable
                ? GovernedLoopSleepCurrentPostureReadStatus.Unavailable
                : GovernedLoopSleepCurrentPostureReadStatus.Conflict);
        }

        DateTimeOffset observedAtUtc;
        try
        {
            observedAtUtc = _timeProvider.GetUtcNow();
        }
        catch
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Unavailable);
        }

        if (observedAtUtc == default
            || observedAtUtc.Offset != TimeSpan.Zero
            || observedAtUtc < run.UpdatedAtUtc
            || observedAtUtc < run.Frontier.Payload.UpdatedAtUtc)
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Unavailable);
        }

        try
        {
            var lifecycleStatus = Map(run.Status);
            if (lifecycleStatus == GovernedLoopRunStatus.Unknown)
            {
                return Result(GovernedLoopSleepCurrentPostureReadStatus.Conflict);
            }

            var lifecycle = GovernedLoopRunLifecycle.Create(
                binding,
                GovernedLoopRunLifecyclePayload.Create(
                    GovernedLoopExecutionLimits.CurrentSchemaVersion,
                    run.LifecycleVersion,
                    lifecycleStatus,
                    run.CreatedAtUtc,
                    run.UpdatedAtUtc,
                    run.IsTerminal ? run.UpdatedAtUtc : null));
            var execution = GovernedLoopExecutionEvidenceSet.Create(
                GovernedLoopExecutionLimits.CurrentSchemaVersion,
                lifecycle,
                run.Frontier,
                [],
                []);
            var active = IsExactActiveResolution(resolution, authorityReference);
            var authorityHash = AuthorityHash(authorityReference, resolution);
            var expiry = ExactGrant(resolution, authorityReference)?.Boundary.ExpiresAtUtc;
            var postureHash = CustomLoopTraceContentHash.Compute(string.Join(
                '\n',
                "governed-wait-posture-v1",
                binding.RunId,
                run.LifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                run.Status.ToString(),
                run.Frontier.Payload.ContentHash,
                adapterBinding.AdmissionReceipt.Intent.Publication.PublicationOperationId,
                adapterBinding.AdmissionReceipt.Intent.Publication.ValidationEvidenceHash,
                authorityHash,
                active ? "unattended" : "review-required",
                expiry?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none"));
            var posture = new GovernedLoopSleepCurrentPosture(
                execution,
                adapterBinding.AdmissionReceipt.Intent.Publication,
                active,
                authorityHash,
                expiry,
                observedAtUtc,
                postureHash);
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Found, posture);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Result(GovernedLoopSleepCurrentPostureReadStatus.Conflict);
        }
    }

    private static bool IsExactActiveResolution(
        AuthorityGrantResolution resolution,
        AuthorityGrantReference reference)
        => resolution.Status == AuthorityGrantResolutionStatus.Active
            && ExactGrant(resolution, reference) is { } grant
            && AuthorityGrantContractValidator.Validate(grant).IsValid
            && AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
            && IsGrantEvidenceHash(resolution.DependencyEvidenceHash);

    private static AuthorityGrant? ExactGrant(
        AuthorityGrantResolution resolution,
        AuthorityGrantReference reference)
        => resolution.RequestedReference == reference
            && resolution.Grant is { } grant
            && grant.GrantId.Equals(reference.GrantId)
            && grant.Revision.Equals(reference.Revision)
            && string.Equals(grant.ContentHash, reference.ContentHash, StringComparison.Ordinal)
                ? grant
                : null;

    private static string AuthorityHash(
        AuthorityGrantReference reference,
        AuthorityGrantResolution resolution)
        => CustomLoopTraceContentHash.Compute(string.Join(
            '\n',
            "governed-wait-authority-v1",
            reference.GrantId.Value,
            reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reference.ContentHash,
            resolution.Status.ToString(),
            resolution.Grant?.ContentHash ?? "none",
            resolution.CurrentGrant?.ContentHash ?? "none",
            resolution.DependencyEvidenceHash ?? string.Empty));

    private static GovernedLoopRunStatus Map(CustomLoopRunStatus status)
        => status switch
        {
            CustomLoopRunStatus.Admitted => GovernedLoopRunStatus.Admitted,
            CustomLoopRunStatus.Running => GovernedLoopRunStatus.Running,
            CustomLoopRunStatus.Waiting => GovernedLoopRunStatus.Waiting,
            CustomLoopRunStatus.PauseRequested => GovernedLoopRunStatus.PauseRequested,
            CustomLoopRunStatus.Paused => GovernedLoopRunStatus.Paused,
            CustomLoopRunStatus.CancelRequested => GovernedLoopRunStatus.CancelRequested,
            CustomLoopRunStatus.Completed => GovernedLoopRunStatus.Completed,
            CustomLoopRunStatus.Failed => GovernedLoopRunStatus.Failed,
            CustomLoopRunStatus.Cancelled => GovernedLoopRunStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => GovernedLoopRunStatus.NeedsReview,
            _ => GovernedLoopRunStatus.Unknown,
        };

    private static bool IsGrantEvidenceHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedLoopSleepCurrentPostureReadResult Result(
        GovernedLoopSleepCurrentPostureReadStatus status,
        GovernedLoopSleepCurrentPosture? posture = null)
        => new(status, posture);
}
