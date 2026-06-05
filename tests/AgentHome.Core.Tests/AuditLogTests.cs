using AgentHome.Core.Audit;
using AgentHome.Core.Workspace;
using Xunit;

namespace AgentHome.Core.Tests;

public sealed class AuditLogTests
{
    [Fact]
    public async Task AppendAsyncWritesNdjsonEvent()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        var auditLog = new AuditLog(paths);

        await auditLog.AppendAsync(new AuditEvent(
            Actor: "test",
            Action: "policy.check",
            Target: "workspace/shared/demo.txt",
            Decision: "Prompt",
            Detail: "Unit test event."));

        var content = await File.ReadAllTextAsync(paths.EventsLogPath);

        Assert.Contains("\"Actor\":\"test\"", content);
        Assert.Contains("\"Action\":\"policy.check\"", content);
        Assert.Contains("\"Decision\":\"Prompt\"", content);
    }

    [Fact]
    public async Task ReadTailAsyncReturnsRequestedNonBlankTail()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        Directory.CreateDirectory(paths.LogsPath);
        await File.WriteAllTextAsync(paths.EventsLogPath, string.Join(Environment.NewLine, new[]
        {
            "{\"Action\":\"one\"}",
            "",
            "{\"Action\":\"two\"}",
            "{\"Action\":\"three\"}"
        }));

        var lines = await new AuditLog(paths).ReadTailAsync(2);

        Assert.Equal(2, lines.Count);
        Assert.Contains("\"two\"", lines[0]);
        Assert.Contains("\"three\"", lines[1]);
    }

    [Fact]
    public async Task ReadTailAsyncReturnsEmptyWhenLogIsMissing()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);

        var lines = await new AuditLog(paths).ReadTailAsync(20);

        Assert.Empty(lines);
    }
}
