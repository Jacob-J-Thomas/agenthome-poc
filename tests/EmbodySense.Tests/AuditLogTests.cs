using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

public sealed class AuditLogTests
{
    [Fact]
    public async Task ReadTailAsync_returns_last_events_in_order()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        await auditLog.AppendAsync(AuditEvent.Create("test", "first", "target", "ok", "first event"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "second", "target", "ok", "second event"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "third", "target", "ok", "third event"));

        var events = await auditLog.ReadTailAsync(2);

        Assert.Collection(
            events,
            auditEvent => Assert.Equal("second", auditEvent.Action),
            auditEvent => Assert.Equal("third", auditEvent.Action));
    }

    [Fact]
    public async Task ReadTailAsync_returns_empty_when_log_file_is_missing()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        var events = await auditLog.ReadTailAsync(10);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadTailAsync_rejects_non_positive_limits()
    {
        using var workspace = new TestWorkspace();
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => auditLog.ReadTailAsync(0));
    }
}
