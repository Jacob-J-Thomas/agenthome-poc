using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Posture;

public sealed class GovernedLoopOperationalContractTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T15:00:00Z");
    private static readonly string _workspaceId = "workspace-sha256:" + new string('1', 64);
    private static readonly string _hash = new('a', 64);

    [Fact]
    public void Request_hash_binds_surface_authority_scope_expected_evidence_and_batch_bound()
    {
        var request = Request();

        Assert.True(GovernedLoopOperationalContract.IsValid(request));
        var hash = GovernedLoopOperationalHash.Request(request);

        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { SurfaceId = "cli" }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { ActorId = "actor-2" }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { WorkspaceId = "workspace-sha256:" + new string('2', 64) }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { ExpectedRevision = 5 }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { ExpectedEvidenceHash = new string('b', 64) }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { ExpectedAuthorityEvidenceHash = new string('c', 64) }));
        Assert.NotEqual(hash, GovernedLoopOperationalHash.Request(request with { MaximumBatchItems = 4 }));
    }

    [Fact]
    public void Receipt_generations_are_predecessor_linked_and_defensively_capture_immutable_progress()
    {
        var request = Request();
        var authority = Authority(request);
        var pending = GovernedLoopOperationalControlReceiptFactory.Create(
            request,
            GovernedLoopOperationalHash.Request(request),
            authority,
            _now,
            _now,
            GovernedLoopOperationalControlReceiptState.Pending,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "operational-control-pending",
            []);
        var mutable = new[]
        {
            new GovernedLoopOperationalControlProgress("delivery-1", 1, _hash, GovernedLoopOperationalControlStatus.OperationInProgress, null, null, "target-captured")
        };

        var successor = GovernedLoopOperationalControlReceiptFactory.Successor(
            pending,
            _now.AddSeconds(1),
            GovernedLoopOperationalControlReceiptState.Mutating,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "targets-captured",
            mutable);
        mutable[0] = mutable[0] with { TargetId = "delivery-tampered" };

        Assert.Null(pending.PreviousContentHash);
        Assert.Equal(pending.ContentHash, successor.PreviousContentHash);
        Assert.Equal("delivery-1", Assert.Single(successor.Progress).TargetId);
        Assert.Equal(successor.ContentHash, GovernedLoopOperationalHash.Receipt(successor));
    }

    [Fact]
    public void Query_bounds_allow_finite_opaque_run_cursor_but_reject_malformed_scope_and_unbounded_values()
    {
        Assert.True(GovernedLoopOperationalContract.IsValid(new GovernedLoopOperationalPostureQuery(1, 2, 3, 4, AfterRunId: "eyJ2ZXJzaW9uIjoxfQ")));
        Assert.True(GovernedLoopOperationalContract.IsValid(new GovernedLoopOperationalPostureQuery(1, 2, 3, 4, QueueCursor: "q1.4.1.ZGVsaXZlcnktMQ")));
        Assert.False(GovernedLoopOperationalContract.IsValid(new GovernedLoopOperationalPostureQuery(1, 2, 3, 4, QueueCursor: "delivery-1")));
        Assert.False(GovernedLoopOperationalContract.IsValid(new GovernedLoopOperationalPostureQuery(1, 2, 3, 101)));
        Assert.False(GovernedLoopOperationalContract.IsValid(Request() with { WorkspaceId = "workspace-1" }));
        Assert.False(GovernedLoopOperationalContract.IsValid(Request() with { SurfaceId = "web browser" }));
    }

    [Theory]
    [InlineData(GovernedLoopOperationalControlKind.PauseRun, "run-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.CancelRun, "run-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.ResumeRun, "run-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.DisableSchedule, "schedule-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.EnableSchedule, "schedule-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.CancelDelivery, "delivery-1", 1, true)]
    [InlineData(GovernedLoopOperationalControlKind.CancelPendingDeliveries, "loop-1", 3, true)]
    [InlineData(GovernedLoopOperationalControlKind.PauseRun, "/", 1, false)]
    [InlineData(GovernedLoopOperationalControlKind.DisableSchedule, "/", 1, false)]
    [InlineData(GovernedLoopOperationalControlKind.CancelDelivery, "/", 1, false)]
    [InlineData(GovernedLoopOperationalControlKind.CancelPendingDeliveries, "/", 3, false)]
    public void Control_targets_use_their_kind_specific_canonical_identity_contract(
        GovernedLoopOperationalControlKind kind,
        string targetId,
        int maximumBatchItems,
        bool expected)
    {
        var request = Request() with { Kind = kind, TargetId = targetId, MaximumBatchItems = maximumBatchItems };

        Assert.Equal(expected, GovernedLoopOperationalContract.IsValid(request));
    }

    private static GovernedLoopOperationalControlRequest Request()
        => new(
            GovernedLoopOperationalControlRequest.CurrentSchemaVersion,
            _workspaceId,
            "operation-1",
            GovernedLoopOperationalControlKind.CancelPendingDeliveries,
            "loop-1",
            4,
            _hash,
            new string('d', 64),
            "actor-1",
            "startup",
            3);

    private static GovernedLoopOperationalControlAuthority Authority(GovernedLoopOperationalControlRequest request)
    {
        const string Reason = "test-authorized";
        return new GovernedLoopOperationalControlAuthority(
            GovernedLoopOperationalControlAuthority.CurrentSchemaVersion,
            request.WorkspaceId,
            request.ActorId,
            request.SurfaceId,
            _now,
            GovernedLoopOperationalHash.Authority(request.WorkspaceId, request.ActorId, request.SurfaceId, _now, true, Reason),
            true,
            Reason);
    }
}
