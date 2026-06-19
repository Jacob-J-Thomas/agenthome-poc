using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolPermissionService
{
    ToolPermissionCheck Evaluate(ToolRequest request);
}
