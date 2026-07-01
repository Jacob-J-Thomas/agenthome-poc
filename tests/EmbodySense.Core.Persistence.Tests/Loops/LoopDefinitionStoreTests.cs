using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class LoopDefinitionStoreTests
{
    [Fact]
    public async Task SaveAsync_writes_loop_definition_json_that_can_be_loaded()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new LoopDefinitionStore(paths);
        var definition = LoopDefinition.CreateDefaultConversation();

        await store.SaveAsync(definition);

        Assert.True(File.Exists(paths.DefaultConversationLoopDefinitionPath));
        var json = await File.ReadAllTextAsync(paths.DefaultConversationLoopDefinitionPath);
        Assert.Contains("\"trigger\": \"human-message\"", json);
        Assert.Contains("\"memoryScope\": \"workspace-startup-context\"", json);
        Assert.Contains("\"reviewPolicy\": \"review-at-authority-boundaries\"", json);
        Assert.Contains("\"failurePolicy\": \"record-failure-and-surface-to-user\"", json);
        Assert.Contains("\"state\": \"enabled\"", json);
        Assert.Contains("\"editMode\": \"system-locked\"", json);
        Assert.Contains("\"entryNodeId\": \"accept-user-message\"", json);
        Assert.Contains("\"kind\": \"model-inference\"", json);
        Assert.Empty(Directory.EnumerateFiles(paths.LoopDefinitionsPath, "*.tmp", SearchOption.TopDirectoryOnly));
        var loaded = await store.LoadAsync("default-conversation");
        Assert.NotNull(loaded);
        Assert.Equal(definition.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(definition.Id, loaded.Id);
        Assert.Equal(definition.DisplayName, loaded.DisplayName);
        Assert.Equal(definition.Description, loaded.Description);
        Assert.Equal(definition.RoleId, loaded.RoleId);
        Assert.Equal(definition.Trigger, loaded.Trigger);
        Assert.Equal(definition.MemoryScope, loaded.MemoryScope);
        Assert.Equal(definition.CapabilityIds, loaded.CapabilityIds);
        Assert.Equal(definition.ReviewPolicy, loaded.ReviewPolicy);
        Assert.Equal(definition.FailurePolicy, loaded.FailurePolicy);
        Assert.Equal(definition.State, loaded.State);
        Assert.Equal(LoopEditMode.SystemLocked, loaded.EditMode);
        Assert.Equal(DefaultConversationLoopGraphIds.AcceptUserMessage, loaded.Graph.EntryNodeId);
        Assert.Contains(loaded.Graph.Nodes, node => node.Id == DefaultConversationLoopGraphIds.DispatchInference && node.Kind == LoopGraphNodeKind.ModelInference);
    }

    [Fact]
    public async Task LoadAsync_rejects_current_definition_without_required_edit_mode()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(paths.DefaultConversationLoopDefinitionPath, """
            {
              "schemaVersion": 1,
              "id": "default-conversation",
              "displayName": "Default conversation loop",
              "description": "The governed loop behind ordinary chat turns in this workspace.",
              "roleId": "default-assistant",
              "trigger": "human-message",
              "memoryScope": "workspace-startup-context",
              "capabilityIds": [
                "conversation.turn",
                "conversation.history"
              ],
              "reviewPolicy": "review-at-authority-boundaries",
              "failurePolicy": "record-failure-and-surface-to-user",
              "state": "enabled"
            }
            """);
        var store = new LoopDefinitionStore(paths);

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation"));

        Assert.Contains("unsupported EditMode value `Unknown`", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_rejects_current_schema_without_graph()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(paths.DefaultConversationLoopDefinitionPath, """
            {
              "schemaVersion": 1,
              "id": "default-conversation",
              "displayName": "Default conversation loop",
              "description": "The governed loop behind ordinary chat turns in this workspace.",
              "roleId": "default-assistant",
              "trigger": "human-message",
              "memoryScope": "workspace-startup-context",
              "capabilityIds": [
                "conversation.turn",
                "conversation.history"
              ],
              "reviewPolicy": "review-at-authority-boundaries",
              "failurePolicy": "record-failure-and-surface-to-user",
              "state": "enabled",
              "editMode": "system-locked"
            }
            """);
        var store = new LoopDefinitionStore(paths);

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation"));

        Assert.Contains("must include a graph", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_returns_definitions_by_id()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var alpha = LoopDefinition.CreateDefaultConversation() with { Id = "alpha-loop", DisplayName = "Alpha loop" };
        var beta = LoopDefinition.CreateDefaultConversation() with { Id = "beta-loop", DisplayName = "Beta loop" };

        await store.SaveAsync(beta);
        await store.SaveAsync(alpha);

        var definitions = await store.ListAsync();

        Assert.Collection(
            definitions,
            definition => Assert.Equal("alpha-loop", definition.Id),
            definition => Assert.Equal("beta-loop", definition.Id));
    }

    [Fact]
    public async Task LoadAsync_returns_null_for_missing_definition()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));

        var definition = await store.LoadAsync("missing-loop");

        Assert.Null(definition);
    }

    [Fact]
    public async Task SaveAsync_rejects_unsafe_definition_ids()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = LoopDefinition.CreateDefaultConversation() with { Id = "../escape" };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(definition));
    }

    [Fact]
    public async Task SaveAsync_rejects_definitions_without_capabilities()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = LoopDefinition.CreateDefaultConversation() with { CapabilityIds = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(definition));
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_graph_definitions()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = LoopDefinition.CreateDefaultConversation() with
        {
            Graph = LoopGraphDefinition.CreateDefaultConversation() with { EntryNodeId = "missing-node" }
        };

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.SaveAsync(definition));

        Assert.Contains("entry node `missing-node` does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_rejects_unknown_enum_values()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = LoopDefinition.CreateDefaultConversation() with { Trigger = LoopTrigger.Unknown };

        await Assert.ThrowsAsync<FormatException>(() => store.SaveAsync(definition));
    }

    [Fact]
    public async Task LoadAsync_rejects_unknown_enum_values()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(paths.DefaultConversationLoopDefinitionPath, """
            {
              "schemaVersion": 1,
              "id": "default-conversation",
              "displayName": "Default conversation loop",
              "description": "The governed loop behind ordinary chat turns in this workspace.",
              "roleId": "default-assistant",
              "trigger": "unapproved-trigger",
              "memoryScope": "workspace-startup-context",
              "capabilityIds": ["conversation.turn"],
              "reviewPolicy": "review-at-authority-boundaries",
              "failurePolicy": "record-failure-and-surface-to-user",
              "state": "enabled"
            }
            """);
        var store = new LoopDefinitionStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation"));
    }

    [Fact]
    public async Task LoadAsync_rejects_unsafe_loop_ids()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../escape"));
    }

    [Fact]
    public async Task LoadAsync_rejects_unsupported_schema_versions()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(paths.DefaultConversationLoopDefinitionPath, """{"schemaVersion":99,"id":"default-conversation"}""");
        var store = new LoopDefinitionStore(paths);

        await Assert.ThrowsAsync<FormatException>(() => store.LoadAsync("default-conversation"));
    }
}
