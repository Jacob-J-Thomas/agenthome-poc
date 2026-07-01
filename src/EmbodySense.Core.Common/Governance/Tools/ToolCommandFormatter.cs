using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

public static class ToolCommandFormatter
{
    public static string Format(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }
}
