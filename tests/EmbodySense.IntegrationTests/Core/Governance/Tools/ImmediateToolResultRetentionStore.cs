using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops;

namespace EmbodySense.IntegrationTests.Core.Governance.Tools;

internal sealed class ImmediateToolResultRetentionStore : IToolResultRetentionStore
{
    public Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(loopDefinition);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolResultRetentionReference(
            ToolResultRetentionStatus.Retained,
            "retained/test-response.json",
            new string('a', 64),
            result.OutputText.Length,
            result.OutputText.Length,
            1,
            DateTimeOffset.UtcNow,
            0,
            "Retained by the deterministic audit-timeout test store."));
    }
}
