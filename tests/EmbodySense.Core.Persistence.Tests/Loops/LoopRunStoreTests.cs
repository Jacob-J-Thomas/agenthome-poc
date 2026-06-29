using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class LoopRunStoreTests
{
    [Fact]
    public async Task SaveAsync_writes_loop_run_json_that_can_be_loaded()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new LoopRunStore(paths);
        var run = CreateRun("run-1", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00"));

        await store.SaveAsync(run);

        Assert.True(File.Exists(Path.Combine(paths.LoopRunsPath, "default-conversation", "run-1.json")));
        var json = await File.ReadAllTextAsync(Path.Combine(paths.LoopRunsPath, "default-conversation", "run-1.json"));
        Assert.Contains("\"status\": \"started\"", json);
        Assert.Contains("\"trigger\": \"human-message\"", json);
        var loaded = await store.LoadAsync("default-conversation", "run-1");
        Assert.NotNull(loaded);
        Assert.Equal(run.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(run.RunId, loaded.RunId);
        Assert.Equal(run.LoopId, loaded.LoopId);
        Assert.Equal(run.RoleId, loaded.RoleId);
        Assert.Equal(run.Status, loaded.Status);
        Assert.Equal(run.Surface, loaded.Surface);
        Assert.Equal(run.Trigger, loaded.Trigger);
        Assert.Equal(run.StartedAtUtc, loaded.StartedAtUtc);
        Assert.Equal(run.CompletedAtUtc, loaded.CompletedAtUtc);
        Assert.Equal(run.FailureDetail, loaded.FailureDetail);
        Assert.Equal(run.Metadata, loaded.Metadata);
    }

    [Fact]
    public async Task LoadAsync_reads_current_draft_loop_run_json()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runDirectory = Path.Combine(paths.LoopRunsPath, "default-conversation");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run-1.json"), """
            {
              "schemaVersion": 1,
              "runId": "run-1",
              "loopId": "default-conversation",
              "roleId": "default-assistant",
              "status": "started",
              "surface": "web",
              "trigger": "human-message",
              "startedAtUtc": "2026-06-28T12:00:00+00:00",
              "completedAtUtc": null,
              "failureDetail": null,
              "metadata": {
                "connection": "test"
              }
            }
            """);
        var store = new LoopRunStore(paths);

        var run = await store.LoadAsync("default-conversation", "run-1");

        Assert.NotNull(run);
        Assert.Equal(LoopRunStatus.Started, run.Status);
        Assert.Equal(LoopTrigger.HumanMessage, run.Trigger);
    }

    [Fact]
    public async Task ListAsync_returns_runs_newest_first()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));
        var older = CreateRun("run-1", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00"));
        var newer = CreateRun("run-2", DateTimeOffset.Parse("2026-06-28T12:05:00+00:00"));

        await store.SaveAsync(older);
        await store.SaveAsync(newer);

        var runs = await store.ListAsync("default-conversation");

        Assert.Collection(
            runs,
            run => Assert.Equal("run-2", run.RunId),
            run => Assert.Equal("run-1", run.RunId));
    }

    [Fact]
    public async Task LoadAsync_returns_null_for_missing_run()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));

        var run = await store.LoadAsync("default-conversation", "missing-run");

        Assert.Null(run);
    }

    [Fact]
    public async Task SaveAsync_rejects_unsafe_run_ids()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));
        var run = CreateRun("../escape", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(run));
    }

    [Fact]
    public async Task SaveAsync_rejects_null_metadata()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));
        var run = CreateRun("run-1", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00")) with { Metadata = null! };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(run));
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_status_values()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));
        var run = CreateRun("run-1", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00")) with { Status = (LoopRunStatus)999 };

        await Assert.ThrowsAsync<FormatException>(() => store.SaveAsync(run));
    }

    [Fact]
    public async Task LoadAsync_rejects_unknown_enum_values()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runDirectory = Path.Combine(paths.LoopRunsPath, "default-conversation");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run-1.json"), """
            {
              "schemaVersion": 1,
              "runId": "run-1",
              "loopId": "default-conversation",
              "roleId": "default-assistant",
              "status": "wandering",
              "surface": "web",
              "trigger": "human-message",
              "startedAtUtc": "2026-06-28T12:00:00+00:00",
              "completedAtUtc": null,
              "failureDetail": null,
              "metadata": {}
            }
            """);
        var store = new LoopRunStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation", "run-1"));
    }

    [Fact]
    public void Run_status_transition_helpers_update_data_record_state()
    {
        var run = CreateRun("run-1", DateTimeOffset.Parse("2026-06-28T12:00:00+00:00"));
        var completedAt = DateTimeOffset.Parse("2026-06-28T12:05:00+00:00");

        var completed = run.Complete(completedAt);
        var failed = run.Fail(completedAt, "provider failure");
        var cancelled = run.Cancel(completedAt, "caller cancelled");

        Assert.Equal(LoopRunStatus.Completed, completed.Status);
        Assert.Equal(completedAt, completed.CompletedAtUtc);
        Assert.Null(completed.FailureDetail);
        Assert.Equal(LoopRunStatus.Failed, failed.Status);
        Assert.Equal("provider failure", failed.FailureDetail);
        Assert.Equal(LoopRunStatus.Cancelled, cancelled.Status);
        Assert.Equal("caller cancelled", cancelled.FailureDetail);
    }

    [Fact]
    public async Task ListAsync_rejects_unsafe_loop_ids()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopRunStore(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ListAsync("../escape"));
    }

    [Fact]
    public async Task LoadAsync_rejects_unsupported_schema_versions()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runDirectory = Path.Combine(paths.LoopRunsPath, "default-conversation");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run-1.json"), """{"schemaVersion":99,"runId":"run-1","loopId":"default-conversation"}""");
        var store = new LoopRunStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation", "run-1"));
    }

    private static LoopRunRecord CreateRun(string runId, DateTimeOffset startedAtUtc)
    {
        return LoopRunRecord.Started(
            runId,
            "default-conversation",
            "default-assistant",
            "web",
            LoopTrigger.HumanMessage,
            startedAtUtc,
            new Dictionary<string, string> { ["connection"] = "test" });
    }
}
