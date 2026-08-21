using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class GovernedLoopOperationalControlReceiptStoreTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T15:00:00Z");
    private static readonly string _workspaceId = "workspace-sha256:" + new string('1', 64);

    [Fact]
    public async Task Persisted_control_receipts_expose_only_value_free_identity_hash_and_progress_fields()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var pending = Pending();
        var begun = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, begun.Status);

        var json = await File.ReadAllTextAsync(Path.Combine(
            paths.GovernedLoopOperationalControlReceiptsPath,
            pending.OperationId + ".json"));
        var properties = JsonNode.Parse(json)!.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "actorId", "authorityEvidenceHash", "contentHash", "expectedEvidenceHash", "expectedRevision",
            "kind", "operationId", "outcome", "previousContentHash", "progress", "reasonCode", "requestHash",
            "requestedAtUtc", "schemaVersion", "state", "surfaceId", "targetId", "updatedAtUtc", "workspaceId"
        }, properties);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Pending_intent_precedes_ownership_and_exact_restart_reclaims_abandoned_owner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var receipt = Pending();
        var store = new GovernedLoopOperationalControlReceiptStore(paths);

        var first = await store.BeginAsync(receipt);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, first.Status);
        Assert.NotNull(first.Lease);
        Assert.True(File.Exists(Path.Combine(paths.GovernedLoopOperationalControlReceiptsPath, receipt.OperationId + ".json")));

        var concurrent = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(receipt);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.OperationInProgress, concurrent.Status);
        first.Lease!.Dispose();

        var restarted = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(receipt);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, restarted.Status);
        Assert.Equal(receipt.ContentHash, restarted.Receipt!.ContentHash);
        Assert.NotNull(restarted.Lease);
        restarted.Lease!.Dispose();
    }

    [Fact]
    public async Task Hash_linked_progress_is_bounded_ordered_immutable_and_terminal_replay_is_exact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopOperationalControlReceiptStore(paths);
        var pending = Pending();
        var begun = await store.BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, begun.Status);

        var captured = GovernedLoopOperationalControlReceiptFactory.Successor(
            pending,
            _now.AddSeconds(1),
            GovernedLoopOperationalControlReceiptState.Mutating,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "targets-captured",
            [Progress("delivery-1"), Progress("delivery-2")]);
        var captureResult = await store.CompareExchangeAsync(pending.ContentHash, captured);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, captureResult.Status);
        Assert.Equal(pending.ContentHash, captureResult.Receipt!.PreviousContentHash);

        var appended = GovernedLoopOperationalControlReceiptFactory.Successor(
            captured,
            _now.AddSeconds(2),
            GovernedLoopOperationalControlReceiptState.Mutating,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "targets-appended",
            [Progress("delivery-1"), Progress("delivery-2"), Progress("delivery-3")]);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, (await store.CompareExchangeAsync(captured.ContentHash, appended)).Status);

        var terminalProgress = captured.Progress
            .Select(item => item with { Status = GovernedLoopOperationalControlStatus.Applied, CurrentRevision = item.ExpectedRevision + 1, CurrentEvidenceHash = item.ExpectedEvidenceHash, ReasonCode = "delivery-cancelled" })
            .ToArray();
        var terminal = GovernedLoopOperationalControlReceiptFactory.Successor(
            captured,
            _now.AddSeconds(3),
            GovernedLoopOperationalControlReceiptState.Complete,
            GovernedLoopOperationalControlStatus.Applied,
            "batch-applied",
            terminalProgress);
        var completed = await store.CompareExchangeAsync(captured.ContentHash, terminal);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, completed.Status);
        Assert.Equal(captured.ContentHash, completed.Receipt!.PreviousContentHash);
        begun.Lease!.Dispose();

        var replay = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, replay.Status);
        Assert.Null(replay.Lease);
        Assert.Equal(terminal.ContentHash, replay.Receipt!.ContentHash);
    }

    [Fact]
    public async Task Begin_compare_exchange_and_replay_detach_progress_from_every_caller_collection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopOperationalControlReceiptStore(paths);
        var pending = Pending();
        var pendingSource = new List<GovernedLoopOperationalControlProgress>();
        var mutablePending = WithProgressCollection(pending, pendingSource);

        var begun = await store.BeginAsync(mutablePending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, begun.Status);
        pendingSource.Add(Progress("delivery-z"));
        Assert.Empty(begun.Receipt!.Progress);
        var begunCollection = Assert.IsAssignableFrom<IList<GovernedLoopOperationalControlProgress>>(begun.Receipt.Progress);
        Assert.Throws<NotSupportedException>(() => begunCollection.Add(Progress("delivery-y")));

        var progressSource = new List<GovernedLoopOperationalControlProgress> { Progress("delivery-1") };
        var canonicalCaptured = GovernedLoopOperationalControlReceiptFactory.Successor(
            pending,
            _now.AddSeconds(1),
            GovernedLoopOperationalControlReceiptState.Mutating,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "targets-captured",
            progressSource);
        var mutableCaptured = WithProgressCollection(canonicalCaptured, progressSource);
        var captured = await store.CompareExchangeAsync(pending.ContentHash, mutableCaptured);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, captured.Status);
        progressSource[0] = Progress("delivery-z");

        Assert.Equal("delivery-1", Assert.Single(captured.Receipt!.Progress).TargetId);
        Assert.Equal(captured.Receipt.ContentHash, GovernedLoopOperationalHash.Receipt(captured.Receipt));
        var capturedCollection = Assert.IsAssignableFrom<IList<GovernedLoopOperationalControlProgress>>(captured.Receipt.Progress);
        Assert.Throws<NotSupportedException>(() => capturedCollection[0] = Progress("delivery-z"));
        begun.Lease!.Dispose();

        var replay = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, replay.Status);
        Assert.Equal("delivery-1", Assert.Single(replay.Receipt!.Progress).TargetId);
        Assert.Equal(canonicalCaptured.ContentHash, replay.Receipt.ContentHash);
        Assert.Equal(replay.Receipt.ContentHash, GovernedLoopOperationalHash.Receipt(replay.Receipt));
        var replayCollection = Assert.IsAssignableFrom<IList<GovernedLoopOperationalControlProgress>>(replay.Receipt.Progress);
        Assert.Throws<NotSupportedException>(() => replayCollection.Clear());
        replay.Lease!.Dispose();
    }

    [Fact]
    public async Task Concurrently_mutating_progress_capture_fails_closed_without_changing_durable_receipt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopOperationalControlReceiptStore(paths);
        var pending = Pending();
        var unstablePending = WithProgressCollection(pending, new ConcurrentMutationProgressList([]));

        var rejectedBegin = await store.BeginAsync(unstablePending);

        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt, rejectedBegin.Status);
        Assert.False(File.Exists(Path.Combine(paths.GovernedLoopOperationalControlReceiptsPath, pending.OperationId + ".json")));

        var begun = await store.BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, begun.Status);
        var captured = GovernedLoopOperationalControlReceiptFactory.Successor(
            pending,
            _now.AddSeconds(1),
            GovernedLoopOperationalControlReceiptState.Mutating,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "targets-captured",
            [Progress("delivery-1")]);
        var unstableCaptured = WithProgressCollection(captured, new ConcurrentMutationProgressList(captured.Progress));

        var rejectedExchange = await store.CompareExchangeAsync(pending.ContentHash, unstableCaptured);

        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt, rejectedExchange.Status);
        begun.Lease!.Dispose();
        var replay = await new GovernedLoopOperationalControlReceiptStore(paths).BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Replayed, replay.Status);
        Assert.Equal(GovernedLoopOperationalControlReceiptState.Pending, replay.Receipt!.State);
        Assert.Empty(replay.Receipt.Progress);
        Assert.Equal(pending.ContentHash, replay.Receipt.ContentHash);
        replay.Lease!.Dispose();
    }

    [Fact]
    public async Task Collision_corruption_quota_and_interrupted_atomic_write_fail_closed_without_replacing_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopOperationalControlReceiptStore(
            paths,
            new() { MaxReceipts = 1, MaxReceiptUtf8Bytes = 32 * 1024 });
        Directory.CreateDirectory(paths.GovernedLoopOperationalControlReceiptsPath);
        var orphan = Path.Combine(paths.GovernedLoopOperationalControlReceiptsPath, $".operation-1.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(orphan, "partial");

        var pending = Pending();
        var begun = await store.BeginAsync(pending);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Committed, begun.Status);
        Assert.False(File.Exists(orphan));

        var collision = Pending(Request() with { SurfaceId = "cli" });
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Conflict, (await store.BeginAsync(collision)).Status);
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Backpressured, (await store.BeginAsync(Pending(Request() with { OperationId = "operation-2" }))).Status);
        begun.Lease!.Dispose();

        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopOperationalControlReceiptsPath, "operation-1.json"), "{}");
        Assert.Equal(GovernedLoopOperationalControlReceiptStoreStatus.Corrupt, (await store.BeginAsync(pending)).Status);
    }

    private static GovernedLoopOperationalControlProgress Progress(string targetId)
        => new(targetId, 1, new string('a', 64), GovernedLoopOperationalControlStatus.OperationInProgress, null, null, "target-captured");

    private static GovernedLoopOperationalControlReceipt WithProgressCollection(
        GovernedLoopOperationalControlReceipt source,
        IReadOnlyList<GovernedLoopOperationalControlProgress> progress)
        => new(
            source.SchemaVersion,
            source.WorkspaceId,
            source.OperationId,
            source.RequestHash,
            source.Kind,
            source.TargetId,
            source.ExpectedRevision,
            source.ExpectedEvidenceHash,
            source.ActorId,
            source.SurfaceId,
            source.AuthorityEvidenceHash,
            source.PreviousContentHash,
            source.RequestedAtUtc,
            source.UpdatedAtUtc,
            source.State,
            source.Outcome,
            source.ReasonCode,
            progress,
            source.ContentHash);

    private static GovernedLoopOperationalControlReceipt Pending(GovernedLoopOperationalControlRequest? request = null)
    {
        request ??= Request();
        const string Reason = "test-authorized";
        var authority = new GovernedLoopOperationalControlAuthority(
            GovernedLoopOperationalControlAuthority.CurrentSchemaVersion,
            request.WorkspaceId,
            request.ActorId,
            request.SurfaceId,
            _now,
            GovernedLoopOperationalHash.Authority(request.WorkspaceId, request.ActorId, request.SurfaceId, _now, true, Reason),
            true,
            Reason);
        return GovernedLoopOperationalControlReceiptFactory.Create(
            request,
            GovernedLoopOperationalHash.Request(request),
            authority,
            _now,
            _now,
            GovernedLoopOperationalControlReceiptState.Pending,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "operational-control-pending",
            []);
    }

    private static GovernedLoopOperationalControlRequest Request()
        => new(
            GovernedLoopOperationalControlRequest.CurrentSchemaVersion,
            _workspaceId,
            "operation-1",
            GovernedLoopOperationalControlKind.CancelPendingDeliveries,
            "loop-1",
            4,
            new string('a', 64),
            new string('d', 64),
            "actor-1",
            "startup",
            3);
}
