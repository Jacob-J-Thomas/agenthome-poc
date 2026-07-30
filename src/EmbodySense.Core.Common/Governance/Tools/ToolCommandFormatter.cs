using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Formats tool commands.
/// </summary>
public static class ToolCommandFormatter
{
    /// <summary>
    /// Formats a governed tool command as its canonical lowercase protocol token.
    /// </summary>
    /// <param name="command">The governed tool command.</param>
    /// <returns>The lowercase command token.</returns>
    public static string Format(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }
}
