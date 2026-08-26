using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewEffectReleaseReadStatusProjectionTests
{
    [Theory]
    [InlineData(GovernedLoopEffectCertaintySnapshotStatus.Missing, HumanReviewEffectReleaseReadStatus.Missing)]
    [InlineData(GovernedLoopEffectCertaintySnapshotStatus.Corrupt, HumanReviewEffectReleaseReadStatus.Corrupt)]
    [InlineData(GovernedLoopEffectCertaintySnapshotStatus.Unavailable, HumanReviewEffectReleaseReadStatus.Unavailable)]
    [InlineData(GovernedLoopEffectCertaintySnapshotStatus.Stale, HumanReviewEffectReleaseReadStatus.Stale)]
    public void Noncurrent_source_results_are_closed_and_fail_closed(GovernedLoopEffectCertaintySnapshotStatus source, HumanReviewEffectReleaseReadStatus expected)
    {
        var snapshot = SnapshotFor(HumanReviewEffectCertainty.NotStarted);

        Assert.Equal(expected, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult(source)));
    }

    [Theory]
    [InlineData(HumanReviewEffectCertainty.NotStarted, HumanReviewEffectReleaseReadStatus.ExactNotStarted)]
    [InlineData(HumanReviewEffectCertainty.Dispatched, HumanReviewEffectReleaseReadStatus.Dispatched)]
    [InlineData(HumanReviewEffectCertainty.Conclusive, HumanReviewEffectReleaseReadStatus.Conclusive)]
    [InlineData(HumanReviewEffectCertainty.Ambiguous, HumanReviewEffectReleaseReadStatus.Ambiguous)]
    [InlineData(HumanReviewEffectCertainty.Terminal, HumanReviewEffectReleaseReadStatus.Terminal)]
    public void Exact_current_snapshot_maps_every_safe_certainty_posture(HumanReviewEffectCertainty certainty, HumanReviewEffectReleaseReadStatus expected)
    {
        var snapshot = SnapshotFor(certainty);

        Assert.Equal(expected, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot)));
    }

    [Fact]
    public void Query_must_match_exact_current_identity_and_preparation_before_not_started_is_release_eligible()
    {
        var expected = SnapshotFor(HumanReviewEffectCertainty.NotStarted);
        var crossEffect = SnapshotFor(HumanReviewEffectCertainty.NotStarted, effectId: "effect-two");
        var changedPreparation = SnapshotFor(HumanReviewEffectCertainty.NotStarted, reviewPayloadHash: 'b');

        Assert.Equal(HumanReviewEffectReleaseReadStatus.ExactNotStarted, HumanReviewEffectReleaseReadStatusProjection.Project(Query(expected), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, expected)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Stale, HumanReviewEffectReleaseReadStatusProjection.Project(Query(expected), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, crossEffect)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Stale, HumanReviewEffectReleaseReadStatusProjection.Project(Query(expected), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, changedPreparation)));
    }

    [Fact]
    public void Malformed_query_and_source_results_never_become_release_eligible()
    {
        var snapshot = SnapshotFor(HumanReviewEffectCertainty.NotStarted);
        var malformedQuery = new GovernedLoopEffectCertaintySnapshotQuery(snapshot.Identity with { IdentityHash = Hash('f') }, snapshot.Preparation);
        var malformedResult = snapshot with { SnapshotHash = Hash('f') };

        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(null, null));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(malformedQuery, new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult((GovernedLoopEffectCertaintySnapshotStatus)99)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Missing, snapshot)));
        Assert.Equal(HumanReviewEffectReleaseReadStatus.Invalid, HumanReviewEffectReleaseReadStatusProjection.Project(Query(snapshot), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, malformedResult)));
    }

    private static GovernedLoopEffectCertaintySnapshotQuery Query(HumanReviewEffectCertaintySnapshot snapshot)
        => new(snapshot.Identity, snapshot.Preparation);

    private static HumanReviewEffectCertaintySnapshot SnapshotFor(HumanReviewEffectCertainty certainty, string effectId = "effect-one", char reviewPayloadHash = 'a')
    {
        var prepared = Attempt(effectId);
        var attempt = certainty switch
        {
            HumanReviewEffectCertainty.NotStarted => prepared,
            HumanReviewEffectCertainty.Dispatched => Dispatched(prepared),
            HumanReviewEffectCertainty.Conclusive => Conclusive(prepared),
            HumanReviewEffectCertainty.Ambiguous => Ambiguous(prepared),
            HumanReviewEffectCertainty.Terminal => Terminal(prepared),
            _ => throw new ArgumentOutOfRangeException(nameof(certainty)),
        };
        var binding = Binding(attempt, null, reviewPayloadHash);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(binding, attempt);
        var reviewed = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(attempt.Payload.EffectId, attempt.Payload.OperationId, attempt.Payload.EffectGeneration, attempt.Payload.IntentHash, preparation.PreparationHash, HumanReviewEffectDispatchCertainty.NotDispatched, string.Empty));
        return HumanReviewEffectReleaseContract.Create(Binding(attempt, reviewed, reviewPayloadHash), attempt, attempt.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Attempt(string effectId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/workspace/read-file", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.2.3", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('a'), out var capabilityHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var revision = GovernedLoopRevisionReference.Create(1, "graph-one", "revision-one", Hash('b'));
        var binding = GovernedLoopExecutionBinding.Create(1, "run-one", revision, 1);
        return GovernedLoopEffectAttemptContract.Prepare(
            binding,
            "node-one",
            1,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, capabilityHash!),
            new CapabilityImplementationIdentity(providerId!, "workspace/read-file"),
            "probe/observe",
            Hash('c'),
            effectId,
            effectId + "-operation",
            1,
            Hash('d'),
            Hash('e'),
            Hash('f'),
            Hash('1'),
            "before-one",
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
    }

    private static GovernedLoopEffectAttempt Dispatched(GovernedLoopEffectAttempt prepared)
    {
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('2'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        return GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(2));
    }

    private static GovernedLoopEffectAttempt Conclusive(GovernedLoopEffectAttempt prepared)
    {
        var dispatched = Dispatched(prepared);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-one", "after-one", prepared.Payload.UpdatedAtUtc.AddSeconds(3));
    }

    private static GovernedLoopEffectAttempt Ambiguous(GovernedLoopEffectAttempt prepared)
    {
        var dispatched = Dispatched(prepared);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(3));
    }

    private static GovernedLoopEffectAttempt Terminal(GovernedLoopEffectAttempt prepared)
    {
        var conclusive = Conclusive(prepared);
        return GovernedLoopEffectAttemptContract.Advance(conclusive, GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-one", "after-one", prepared.Payload.UpdatedAtUtc.AddSeconds(4));
    }

    private static HumanReviewBinding Binding(GovernedLoopEffectAttempt attempt, HumanReviewEffectAttemptBinding? effect, char reviewPayloadHash = 'a')
        => HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            "workspace-sha256:" + Hash('0'),
            attempt.Binding.RunId,
            attempt.Binding.Revision.GraphId,
            attempt.Binding.Revision.RevisionId,
            attempt.Binding.Revision.ExecutableHash,
            attempt.NodeId,
            0,
            null,
            attempt.NodeAttempt,
            "frontier-one",
            1,
            Hash('3'),
            Hash('4'),
            Hash('5'),
            Hash('6'),
            Hash('7'),
            Hash('8'),
            Hash('9'),
            Hash(reviewPayloadHash),
            effect,
            string.Empty));

    private static string Hash(char value) => new(value, 64);
}
