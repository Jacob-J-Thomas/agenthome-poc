using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolResultRetentionStore
{
    Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default);
}
