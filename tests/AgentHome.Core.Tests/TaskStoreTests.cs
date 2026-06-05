using System.Text.Json;
using AgentHome.Core.Tasks;
using AgentHome.Core.Workspace;
using Xunit;

namespace AgentHome.Core.Tests;

public sealed class TaskStoreTests
{
    [Fact]
    public async Task StartAsyncCreatesTaskJson()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        await new WorkspaceInitializer().InitializeAsync(workspace.Root);
        var store = new TaskStore(paths);

        var task = await store.StartAsync("  Refactor authentication middleware  ");
        var taskPath = workspace.Path($".agent/tasks/{task.Id}/task.json");

        Assert.True(File.Exists(taskPath));

        using var taskJson = JsonDocument.Parse(await File.ReadAllTextAsync(taskPath));
        Assert.Equal(task.Id, taskJson.RootElement.GetProperty("Id").GetString());
        Assert.Equal("Refactor authentication middleware", taskJson.RootElement.GetProperty("Goal").GetString());
        Assert.Equal("active", taskJson.RootElement.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Array, taskJson.RootElement.GetProperty("Constraints").ValueKind);
        Assert.Equal(JsonValueKind.Array, taskJson.RootElement.GetProperty("Decisions").ValueKind);
        Assert.Equal(JsonValueKind.Array, taskJson.RootElement.GetProperty("Artifacts").ValueKind);
    }

    [Fact]
    public async Task StartAsyncAppendsTaskStartAuditEvent()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        await new WorkspaceInitializer().InitializeAsync(workspace.Root);
        var store = new TaskStore(paths);

        var task = await store.StartAsync("Refactor authentication middleware");

        var audit = await File.ReadAllTextAsync(paths.EventsLogPath);
        Assert.Contains("\"Action\":\"task.start\"", audit);
        Assert.Contains($"\"TaskId\":\"{task.Id}\"", audit);
        Assert.Contains("\"Decision\":\"Allow\"", audit);
    }

    [Fact]
    public async Task StartAsyncRejectsBlankGoal()
    {
        using var workspace = TestWorkspace.Create();
        var store = new TaskStore(new WorkspacePaths(workspace.Root));

        await Assert.ThrowsAsync<ArgumentException>(() => store.StartAsync("   "));
    }
}
