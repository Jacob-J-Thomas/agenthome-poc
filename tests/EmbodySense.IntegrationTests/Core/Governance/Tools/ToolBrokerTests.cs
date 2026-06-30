using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.Core.Governance.Tools;

public sealed class ToolBrokerTests
{
    [Fact]
    public async Task ExecuteAsync_reads_allowed_file_and_records_audit_events()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var target = workspace.File("shared", "note.txt");
        await File.WriteAllTextAsync(target, "hello from shared");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt"));

        Assert.True(result.Succeeded);
        Assert.Equal("hello from shared", result.OutputText);
        var events = await ReadAuditAsync(workspace);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.permission.evaluate" && auditEvent.Outcome == "allowed");
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.execute" && auditEvent.Outcome == "succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_denies_explicitly_denied_file_without_prompting()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var target = workspace.File("private", "secret.txt");
        await File.WriteAllTextAsync(target, "private");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "private/secret.txt"));

        Assert.Equal(ToolExecutionOutcome.Denied, result.Outcome);
        Assert.StartsWith("denied:", result.OutputText, StringComparison.Ordinal);
        var events = await ReadAuditAsync(workspace);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.permission.evaluate" && auditEvent.Outcome == "denied");
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.execute" && auditEvent.Outcome == "denied");
    }

    [Fact]
    public async Task ExecuteAsync_prompts_and_runs_approval_required_write()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var prompt = new FixedApprovalPrompt(ToolApprovalResponse.Approve("test", "approved in test"));
        var broker = CreateBroker(workspace, prompt);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, ".agent/skills/generated.md", "skill notes"));

        Assert.True(result.Succeeded);
        Assert.Equal("wrote 11 characters", result.OutputText);
        Assert.Single(prompt.Requests);
        Assert.Equal("skill notes", await File.ReadAllTextAsync(workspace.File(".agent", "skills", "generated.md")));
        var events = await ReadAuditAsync(workspace);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.approval.request" && auditEvent.Outcome == "requested");
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.approval.decision" && auditEvent.Outcome == "approved");
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.execute" && auditEvent.Outcome == "succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_rejected_approval_does_not_write_file()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var prompt = new FixedApprovalPrompt(ToolApprovalResponse.Reject("test", "rejected in test"));
        var broker = CreateBroker(workspace, prompt);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, ".agent/skills/generated.md", "skill notes"));

        Assert.Equal(ToolExecutionOutcome.ApprovalRejected, result.Outcome);
        Assert.False(File.Exists(workspace.File(".agent", "skills", "generated.md")));
        var events = await ReadAuditAsync(workspace);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.approval.decision" && auditEvent.Outcome == "rejected");
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.execute" && auditEvent.Outcome == "approval_rejected");
    }

    [Fact]
    public async Task ExecuteAsync_searches_readable_workspace_content()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("shared", "one.txt"), "alpha" + Environment.NewLine + "beta");
        await File.WriteAllTextAsync(workspace.File("shared", "two.txt"), "gamma");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Search, "shared", Pattern: "alp"));

        Assert.True(result.Succeeded);
        Assert.Contains("one.txt:1: alpha", result.OutputText);
        Assert.DoesNotContain("two.txt", result.OutputText);
    }

    [Fact]
    public async Task ExecuteAsync_lists_directories_before_files()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        Directory.CreateDirectory(workspace.File("shared", "folder"));
        await File.WriteAllTextAsync(workspace.File("shared", "note.txt"), "note");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.List, "shared"));

        Assert.True(result.Succeeded);
        Assert.Equal("folder\\" + Environment.NewLine + "note.txt", result.OutputText.Replace("/", "\\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_appends_to_allowed_existing_file()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("shared", "note.txt"), "first");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Append, "shared/note.txt", " second"));

        Assert.True(result.Succeeded);
        Assert.Equal("appended 7 characters", result.OutputText);
        Assert.Equal("first second", await File.ReadAllTextAsync(workspace.File("shared", "note.txt")));
    }

    [Fact]
    public async Task ExecuteAsync_returns_failed_result_for_missing_allowed_read()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/missing.txt"));

        Assert.Equal(ToolExecutionOutcome.Failed, result.Outcome);
        Assert.StartsWith("failed:", result.OutputText, StringComparison.Ordinal);
        var events = await ReadAuditAsync(workspace);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.execute" && auditEvent.Outcome == "failed");
    }

    [Fact]
    public async Task ExecuteAsync_deletes_directory_after_approval()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        Directory.CreateDirectory(workspace.File("shared", "delete-me"));
        await File.WriteAllTextAsync(workspace.File("shared", "delete-me", "note.txt"), "temporary");
        var prompt = new FixedApprovalPrompt(ToolApprovalResponse.Approve("test", "approved delete in test"));
        var broker = CreateBroker(workspace, prompt);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Delete, "shared/delete-me"));

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(workspace.File("shared", "delete-me")));
    }

    [Fact]
    public async Task ExecuteAsync_deletes_only_after_default_approval()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("shared", "delete-me.txt"), "temporary");
        var prompt = new FixedApprovalPrompt(ToolApprovalResponse.Approve("test", "approved delete in test"));
        var broker = CreateBroker(workspace, prompt);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Delete, "shared/delete-me.txt"));

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(workspace.File("shared", "delete-me.txt")));
        Assert.Single(prompt.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_denies_paths_outside_workspace_root()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var outsidePath = Path.Combine(Path.GetTempPath(), "embodysense-outside.txt");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, outsidePath));

        Assert.Equal(ToolExecutionOutcome.Denied, result.Outcome);
        Assert.Contains("workspace root", result.OutputText);
    }

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt prompt)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = new PermissionPolicyStore().Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt, new LocalWorkspaceClient(paths), new AuditLog(paths));
    }

    private static Task<IReadOnlyList<AuditEvent>> ReadAuditAsync(TestWorkspace workspace)
    {
        return new AuditLog(new WorkspacePaths(workspace.RootPath)).ReadTailAsync(20);
    }

    private sealed class ThrowingApprovalPrompt : IToolApprovalPrompt
    {
        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Approval prompt should not have been called.");
        }
    }

    private sealed class FixedApprovalPrompt(ToolApprovalResponse response) : IToolApprovalPrompt
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
