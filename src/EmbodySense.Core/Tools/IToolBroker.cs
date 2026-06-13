using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Core.Tools;

public interface IToolBroker
{
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
