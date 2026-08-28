using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Projects current Human Review effect evidence and certainty from canonical run and effect-attempt reads.</summary>
/// <remarks>This source owns no effect transition. Every result is reconstructed from the current persisted Human Review binding and a read-only effect-attempt head, so caller-supplied evidence can never become proof.</remarks>
public sealed class CanonicalHumanReviewEffectEvidenceSource : IHumanReviewCurrentEffectAttemptEvidenceSource, IGovernedLoopEffectCertaintySnapshotSource
{
    private readonly ICustomLoopRunStore _runs;
    private readonly IGovernedLoopEffectAttemptReadStore _attempts;

    /// <summary>Initializes the read-only projection over canonical run and effect-attempt stores.</summary>
    /// <param name="runs">The canonical run source that retains the reviewed binding.</param>
    /// <param name="attempts">The non-mutating canonical current effect-attempt source.</param>
    public CanonicalHumanReviewEffectEvidenceSource(ICustomLoopRunStore runs, IGovernedLoopEffectAttemptReadStore attempts)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
    }

    /// <inheritdoc />
    public async Task<HumanReviewCurrentEffectAttemptEvidenceReadResult> ReadAsync(HumanReviewCurrentEffectAttemptEvidenceQuery query, CancellationToken cancellationToken = default)
    {
        if (query?.Binding is null || query.EffectAttempt is null || !HumanReviewContractHash.MatchesBinding(query.Binding) || !HumanReviewContractHash.MatchesEffectAttempt(query.EffectAttempt))
        {
            return new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt);
        }

        var source = await ReadSourceAsync(query.Binding.WorkspaceId, query.Binding.RunId, query.EffectAttempt.OperationId, query.EffectAttempt.EffectGeneration, cancellationToken).ConfigureAwait(false);
        if (source.Status != GovernedLoopEffectAttemptReadStatus.Current || source.Run is null || source.Attempt is null)
        {
            return new HumanReviewCurrentEffectAttemptEvidenceReadResult(MapEvidenceStatus(source.Status));
        }
        if (!TryGetCanonicalBinding(source.Run, out var binding, out var reviewed)
            || !MatchesCanonicalExecutionGeneration(source.Run, source.Attempt)
            || !Equals(binding, query.Binding)
            || !Equals(reviewed, query.EffectAttempt))
        {
            return new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale);
        }
        if (!TryCreateEvidence(binding!, reviewed!, source.Attempt, out var evidence))
        {
            return new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt);
        }

        return new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, evidence);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectCertaintySnapshotResult> ReadAsync(GovernedLoopEffectCertaintySnapshotQuery query, CancellationToken cancellationToken = default)
    {
        if (query?.Identity is null || query.Preparation is null
            || !HumanReviewEffectReleaseContract.TryCaptureExpectation(query.Identity, query.Preparation, out var expectedIdentity, out var expectedPreparation, out _)
            || expectedIdentity is null
            || expectedPreparation is null)
        {
            return new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Corrupt);
        }

        var source = await ReadSourceAsync(expectedIdentity.WorkspaceId, expectedIdentity.RunId, expectedIdentity.OperationId, expectedIdentity.EffectGeneration, cancellationToken).ConfigureAwait(false);
        if (source.Status != GovernedLoopEffectAttemptReadStatus.Current || source.Run is null || source.Attempt is null)
        {
            return new GovernedLoopEffectCertaintySnapshotResult(MapCertaintyStatus(source.Status));
        }
        if (!TryGetCanonicalBinding(source.Run, out var binding, out var reviewed)
            || !MatchesCanonicalExecutionGeneration(source.Run, source.Attempt)
            || !TryCreateSnapshot(binding!, reviewed!, source.Attempt, out var snapshot))
        {
            return new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Corrupt);
        }
        if (!Equals(snapshot!.Identity, expectedIdentity) || !Equals(snapshot.Preparation, expectedPreparation))
        {
            return new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Stale);
        }

        return new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot);
    }

    private async Task<CanonicalHumanReviewEffectSourceRead> ReadSourceAsync(string workspaceId, string runId, string operationId, long effectGeneration, CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        try
        {
            run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return new CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus.Corrupt, null, null);
        }
        catch
        {
            return new CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus.Unavailable, null, null);
        }

        if (run is null)
        {
            return new CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus.Missing, null, null);
        }

        GovernedLoopEffectAttemptReadResult? attempt;
        try
        {
            attempt = await _attempts.ReadAsync(workspaceId, operationId, effectGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus.Unavailable, null, null);
        }

        if (attempt is null || !Enum.IsDefined(attempt.Status))
        {
            return new CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus.Corrupt, run, null);
        }
        return new CanonicalHumanReviewEffectSourceRead(attempt.Status, run, attempt.Attempt);
    }

    private static bool TryGetCanonicalBinding(CustomLoopRunRecord run, out HumanReviewBinding? binding, out HumanReviewEffectAttemptBinding? reviewed)
    {
        binding = null;
        reviewed = null;
        try
        {
            if (!CustomLoopRunValidator.Validate(run).IsValid
                || run.Status != CustomLoopRunStatus.Paused
                || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
                || run.HumanReview is not { } review
                || !HumanReviewContractSnapshot.TryCaptureRequest(review.Request, out var request, out _) || request?.Binding.EffectAttempt is not { } effect
                || !HumanReviewContractHash.MatchesBinding(request.Binding)
                || !HumanReviewContractHash.MatchesEffectAttempt(effect)
                || !string.Equals(run.Id, request.Binding.RunId, StringComparison.Ordinal)
                || !MatchesFrontier(run.Frontier, request.Binding))
            {
                return false;
            }

            binding = request.Binding;
            reviewed = effect;
            return true;
        }
        catch
        {
            binding = null;
            reviewed = null;
            return false;
        }
    }

    private static bool TryCreateEvidence(HumanReviewBinding binding, HumanReviewEffectAttemptBinding reviewed, GovernedLoopEffectAttempt attempt, out HumanReviewCurrentEffectAttemptEvidence? evidence)
    {
        evidence = null;
        try
        {
            if (GovernedLoopEffectAttemptContract.Validate(attempt) is not null || !MatchesReviewedAttempt(binding, reviewed, attempt))
            {
                return false;
            }

            var identity = HumanReviewEffectReleaseContract.CreateIdentity(binding, attempt);
            var preparation = HumanReviewEffectReleaseContract.CreatePreparation(binding, attempt);
            if (!string.Equals(identity.EffectId, reviewed.EffectAttemptId, StringComparison.Ordinal)
                || !string.Equals(identity.OperationId, reviewed.OperationId, StringComparison.Ordinal)
                || identity.EffectGeneration != reviewed.EffectGeneration
                || !string.Equals(identity.IntentHash, reviewed.IntentHash, StringComparison.Ordinal)
                || !string.Equals(preparation.PreparationHash, reviewed.PreparationHash, StringComparison.Ordinal))
            {
                return false;
            }

            evidence = new HumanReviewCurrentEffectAttemptEvidence(identity, preparation);
            return true;
        }
        catch
        {
            evidence = null;
            return false;
        }
    }

    private static bool TryCreateSnapshot(HumanReviewBinding binding, HumanReviewEffectAttemptBinding reviewed, GovernedLoopEffectAttempt attempt, out HumanReviewEffectCertaintySnapshot? snapshot)
    {
        snapshot = null;
        if (!TryCreateEvidence(binding, reviewed, attempt, out _))
        {
            return false;
        }

        try
        {
            snapshot = HumanReviewEffectReleaseContract.Create(binding, attempt, attempt.Payload.UpdatedAtUtc);
            return HumanReviewEffectReleaseContract.TryCapture(snapshot, out snapshot, out _) && snapshot is not null;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool MatchesFrontier(GovernedLoopFrontierPosture frontier, HumanReviewBinding binding)
    {
        var blocked = frontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked).Take(2).ToArray();
        return blocked.Length == 1
            && string.Equals(frontier.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.RunId, binding.RunId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.GraphId, binding.GraphId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.RevisionId, binding.RevisionId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.ExecutableHash, binding.RevisionHash, StringComparison.Ordinal)
            && frontier.Payload.FrontierVersion == binding.FrontierVersion
            && string.Equals(frontier.Payload.ContentHash, binding.FrontierHash, StringComparison.Ordinal)
            && string.Equals(blocked[0].NodeId, binding.NodeId, StringComparison.Ordinal)
            && blocked[0].Attempt == binding.Attempt
            && (binding.ActivationOrdinal is null || blocked[0].ActivationOrdinal == binding.ActivationOrdinal)
            && (binding.VisitOrdinal is null || blocked[0].VisitOrdinal == binding.VisitOrdinal);
    }

    private static bool MatchesCanonicalExecutionGeneration(CustomLoopRunRecord run, GovernedLoopEffectAttempt attempt)
        => run.SequentialAdapterBinding is { } adapter
            && run.Frontier is { } frontier
            && attempt.Binding.ExecutionGeneration == adapter.ExecutionBinding.ExecutionGeneration
            && attempt.Binding.ExecutionGeneration == frontier.Binding.ExecutionGeneration;

    private static bool MatchesReviewedAttempt(HumanReviewBinding binding, HumanReviewEffectAttemptBinding reviewed, GovernedLoopEffectAttempt attempt)
        => string.Equals(attempt.Binding.RunId, binding.RunId, StringComparison.Ordinal)
            && string.Equals(attempt.Binding.Revision.GraphId, binding.GraphId, StringComparison.Ordinal)
            && string.Equals(attempt.Binding.Revision.RevisionId, binding.RevisionId, StringComparison.Ordinal)
            && string.Equals(attempt.Binding.Revision.ExecutableHash, binding.RevisionHash, StringComparison.Ordinal)
            && string.Equals(attempt.NodeId, binding.NodeId, StringComparison.Ordinal)
            && attempt.NodeAttempt == binding.Attempt
            && string.Equals(attempt.Payload.EffectId, reviewed.EffectAttemptId, StringComparison.Ordinal)
            && string.Equals(attempt.Payload.OperationId, reviewed.OperationId, StringComparison.Ordinal)
            && attempt.Payload.EffectGeneration == reviewed.EffectGeneration
            && string.Equals(attempt.Payload.IntentHash, reviewed.IntentHash, StringComparison.Ordinal);

    private static HumanReviewCurrentEffectAttemptEvidenceReadStatus MapEvidenceStatus(GovernedLoopEffectAttemptReadStatus status)
        => status switch
        {
            GovernedLoopEffectAttemptReadStatus.Missing => HumanReviewCurrentEffectAttemptEvidenceReadStatus.Missing,
            GovernedLoopEffectAttemptReadStatus.Corrupt => HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt,
            GovernedLoopEffectAttemptReadStatus.Unavailable => HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable,
            _ => HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt,
        };

    private static GovernedLoopEffectCertaintySnapshotStatus MapCertaintyStatus(GovernedLoopEffectAttemptReadStatus status)
        => status switch
        {
            GovernedLoopEffectAttemptReadStatus.Missing => GovernedLoopEffectCertaintySnapshotStatus.Missing,
            GovernedLoopEffectAttemptReadStatus.Corrupt => GovernedLoopEffectCertaintySnapshotStatus.Corrupt,
            GovernedLoopEffectAttemptReadStatus.Unavailable => GovernedLoopEffectCertaintySnapshotStatus.Unavailable,
            _ => GovernedLoopEffectCertaintySnapshotStatus.Corrupt,
        };
}
