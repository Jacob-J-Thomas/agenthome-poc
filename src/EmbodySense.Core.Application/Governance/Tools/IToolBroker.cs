using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolBroker
{
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
