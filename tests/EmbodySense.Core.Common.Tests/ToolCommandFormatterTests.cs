using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class ToolCommandFormatterTests
{
    [Theory]
    [InlineData(ToolCommand.List, "list")]
    [InlineData(ToolCommand.Read, "read")]
    [InlineData(ToolCommand.Search, "search")]
    [InlineData(ToolCommand.Write, "write")]
    [InlineData(ToolCommand.Append, "append")]
    [InlineData(ToolCommand.Delete, "delete")]
    public void Format_returns_provider_and_capability_safe_command_text(ToolCommand command, string expected)
    {
        Assert.Equal(expected, ToolCommandFormatter.Format(command));
        Assert.Equal("workspace.command." + expected, LoopCapabilityIds.WorkspaceCommandFor(command));
    }
}
