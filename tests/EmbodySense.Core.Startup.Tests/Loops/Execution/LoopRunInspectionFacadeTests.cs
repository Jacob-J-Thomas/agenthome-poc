using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class LoopRunInspectionFacadeTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");

    [Fact]
    public async Task Read_only_facade_exposes_empty_quota_but_cannot_delete()
    {
        using var workspace = new TestWorkspace();
        await using var facade = new LoopRunInspectionFacade(workspace.RootPath);

        Assert.Null(await facade.GetTraceAsync("run-missing"));
        var quota = await facade.GetTraceQuotaAsync();
        Assert.Equal(0, quota.LiveTraceCount);
        Assert.Equal(0, quota.TombstoneCount);
        Assert.Equal(CustomLoopLimits.MaxRunTracesPerWorkspace, quota.MaximumLiveTraceCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.DeleteTraceAsync("run-alpha", new string('a', 64), "delete-trace"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.RecoverInterruptedRunsAsync());
        Assert.Throws<ArgumentException>(() => new LoopRunInspectionFacade(workspace.RootPath, "actor-user"));
    }

    [Fact]
    public async Task Authenticated_facade_recovers_interrupted_runs_before_inspection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var interrupted = await CreateInterruptedRunAsync(store);
        await using var facade = new LoopRunInspectionFacade(workspace.RootPath, "actor-user", "web");

        Assert.Equal("Admitted", (await facade.GetAsync(interrupted.Id))!.Status);
        var recovery = await facade.RecoverInterruptedRunsAsync();
        var recovered = await facade.GetAsync(interrupted.Id);

        Assert.True(recovery.Completed);
        Assert.False(recovery.PreserveCurrentConversation);
        Assert.Equal("Paused", recovered!.Status);
        Assert.Equal(interrupted.LifecycleVersion + 1, recovered.LifecycleVersion);
        Assert.Contains("Restart recovery parked the admitted run", await File.ReadAllTextAsync(paths.EventsLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_facade_projects_exact_trace_quota_and_audited_tombstone_deletion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var terminal = await CreateTerminalRunAsync(store);
        await using var facade = new LoopRunInspectionFacade(workspace.RootPath, "actor-user", "web");
        var trace = await facade.GetTraceAsync(terminal.Id);
        Assert.NotNull(trace);
        Assert.False(trace.IsDeleted);
        Assert.True(trace.PersistedArtifactUtf8Bytes > 0);
        Assert.Equal(1, (await facade.GetTraceQuotaAsync()).LiveTraceCount);

        var deleted = await facade.DeleteTraceAsync(terminal.Id, trace.PersistedArtifactHash, "delete-trace");
        var replay = await facade.DeleteTraceAsync(terminal.Id, trace.PersistedArtifactHash, "delete-trace");
        var tombstone = await facade.GetTraceAsync(terminal.Id);
        var recent = Assert.Single(await facade.ListRecentAsync());
        var quota = await facade.GetTraceQuotaAsync();

        Assert.Equal("Deleted", deleted.Status);
        Assert.True(deleted.IsCommitted);
        Assert.Equal("actor-user", deleted.Tombstone!.DeletionActor);
        Assert.Equal("web", deleted.Tombstone.DeletionSurface);
        Assert.Equal("Complete", deleted.Tombstone.OutcomeIntegrity);
        Assert.Equal("Replayed", replay.Status);
        Assert.True(tombstone!.IsDeleted);
        Assert.True(recent.IsDeleted);
        Assert.Equal(terminal.Id, recent.Id);
        Assert.Equal(terminal.AdmissionOperationId, recent.AdmissionOperationId);
        Assert.Equal(terminal.Status.ToString(), recent.Status);
        Assert.Equal(trace.PersistedArtifactHash, tombstone.OriginalTraceHash);
        Assert.Equal(0, quota.LiveTraceCount);
        Assert.Equal(1, quota.TombstoneCount);
        Assert.Equal(quota.TombstoneUtf8Bytes, quota.AccountedUtf8Bytes);
        var audit = await File.ReadAllTextAsync(paths.EventsLogPath);
        Assert.Contains("loop.trace.deletion.intent", audit, StringComparison.Ordinal);
        Assert.Contains("loop.trace.deletion.outcome", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("Initial prompt", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Facade_surfaces_expected_hash_rejection_without_deleting_trace()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var terminal = await CreateTerminalRunAsync(store);
        await using var facade = new LoopRunInspectionFacade(workspace.RootPath, "actor-user", "web");

        var result = await facade.DeleteTraceAsync(terminal.Id, new string('a', 64), "delete-trace");

        Assert.Equal("HashMismatch", result.Status);
        Assert.False(result.IsCommitted);
        Assert.False((await facade.GetTraceAsync(terminal.Id))!.IsDeleted);
    }

    private static async Task<CustomLoopRunRecord> CreateTerminalRunAsync(CustomLoopRunStore store)
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-alpha", "default-role", "step-1", "create-loop", Timestamp);
        var admittedEvent = Event(1, "event-1", CustomLoopRunEventKind.Admitted, Timestamp);
        var admitted = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-alpha", definition.Id, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-alpha", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(Timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admittedEvent], null, null, null);
        admitted = CustomLoopAdmissionRequestHash.Apply(admitted);
        await store.CreateAsync(admitted);
        var running = Advance(admitted, CustomLoopRunStatus.Running);
        await store.UpdateAsync(running, admitted.LifecycleVersion);
        var completed = Advance(running, CustomLoopRunStatus.Completed);
        await store.UpdateAsync(completed, running.LifecycleVersion);
        return completed;
    }

    private static async Task<CustomLoopRunRecord> CreateInterruptedRunAsync(CustomLoopRunStore store)
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-interrupted", "default-role", "step-1", "create-interrupted-loop", Timestamp);
        var admittedEvent = Event(1, "interrupted-admitted", CustomLoopRunEventKind.Admitted, Timestamp);
        var admitted = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-interrupted", definition.Id, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-interrupted", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(Timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admittedEvent], null, null, null);
        admitted = CustomLoopAdmissionRequestHash.Apply(admitted);
        Assert.True(CustomLoopRunValidator.Validate(admitted).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var audited = admitted with
        {
            LifecycleVersion = 2,
            Events = [admittedEvent, Event(2, "interrupted-admission-audit", CustomLoopRunEventKind.AdmissionAuditCompleted, Timestamp)]
        };
        Assert.True(CustomLoopRunValidator.Validate(audited).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, admitted.LifecycleVersion)).Status);
        return audited;
    }

    private static CustomLoopRunRecord Advance(CustomLoopRunRecord run, CustomLoopRunStatus status)
    {
        var updatedAt = run.UpdatedAtUtc.AddMinutes(1);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = updatedAt,
            CompletedAtUtc = status == CustomLoopRunStatus.Completed ? updatedAt : null,
            ExecutionClock = status == CustomLoopRunStatus.Running ? new CustomLoopExecutionClock(0, updatedAt) : new CustomLoopExecutionClock(1_000, null),
            Events = [.. run.Events, Event(run.Events.Length + 1L, $"event-{run.Events.Length + 1}", CustomLoopRunEventKind.LifecycleChanged, updatedAt)],
            FinalOutput = status == CustomLoopRunStatus.Completed ? "done" : null
        };
    }

    private static CustomLoopRunEvent Event(long sequence, string id, CustomLoopRunEventKind kind, DateTimeOffset timestamp) => new(sequence, id, timestamp, kind, null, null, null, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);
}
