using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Core.Tools;

public interface IToolPermissionService
{
    ToolPermissionCheck Evaluate(ToolRequest request);
}
