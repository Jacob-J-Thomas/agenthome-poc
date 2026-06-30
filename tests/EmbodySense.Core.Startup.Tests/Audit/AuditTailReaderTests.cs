using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Startup.Audit;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Audit;

public sealed class AuditTailReaderTests
{
    [Fact]
    public async Task ReadTailAsync_returns_log_path_and_recent_events()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var auditLog = new AuditLog(paths);
        await auditLog.AppendAsync(AuditEvent.Create("test", "first", "target", "ok", "first detail"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "second", "target", "ok", "second detail"));

        var tail = await new AuditTailReader().ReadTailAsync(workspace.RootPath, limit: 1);

        Assert.Equal(paths.EventsLogPath, tail.EventsLogPath);
        var auditEvent = Assert.Single(tail.Events);
        Assert.Equal("second", auditEvent.Action);
    }

    [Fact]
    public async Task ReadTailAsync_returns_empty_events_for_missing_log()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var tail = await new AuditTailReader().ReadTailAsync(workspace.RootPath, limit: 10);

        Assert.Equal(paths.EventsLogPath, tail.EventsLogPath);
        Assert.Empty(tail.Events);
    }
}
