using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Tools;

public sealed class ToolResultRetentionServiceTests
{
    [Fact]
    public async Task RetainAsync_preserves_the_actual_outcome_when_the_post_retention_audit_fails()
    {
        var audit = new ThrowingAuditLog();
        var service = new ToolResultRetentionService(audit, LoopDefinition.CreateDefaultConversation(), new RetainedStore());
        var result = new ToolResult(
            ToolExecutionOutcome.Succeeded,
            "wrote 4 characters",
            new string('a', 32),
            "/workspace/shared/note.txt",
            new ToolRequest(ToolCommand.Write, "shared/note.txt", "note"));

        var retained = await service.RetainAsync(result);

        Assert.Equal(ToolExecutionOutcome.Succeeded, retained.Outcome);
        Assert.Equal(ToolResultRetentionStatus.Retained, retained.Retention?.Status);
        Assert.Contains("retention audit could not be appended (IOException)", retained.Retention?.Detail, StringComparison.Ordinal);
        Assert.Equal(1, audit.AppendAttempts);
    }

    private sealed class RetainedStore : IToolResultRetentionStore
    {
        public Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolResultRetentionReference(
                ToolResultRetentionStatus.Retained,
                ".agent/logs/tool-responses/manifest.json",
                new string('b', 64),
                result.OutputText.Length,
                result.OutputText.Length,
                1,
                DateTimeOffset.UtcNow,
                0,
                "Retained."));
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public int AppendAttempts { get; private set; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            AppendAttempts++;
            throw new IOException("audit unavailable");
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>([]);
        }
    }
}
