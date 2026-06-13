using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Tests;

public sealed class AgentToolProtocolTests
{
    [Fact]
    public void ParseRequests_parses_single_tool_block()
    {
        var requests = AgentToolProtocol.ParseRequests(
            """
            ```embodysense-tool
            {"tool":"read","path":"workspace/shared/note.txt"}
            ```
            """);

        var request = Assert.Single(requests);
        Assert.Equal(ToolCommand.Read, request.Command);
        Assert.Equal("workspace/shared/note.txt", request.TargetPath);
    }

    [Fact]
    public void ParseRequests_parses_multiple_tool_block_with_aliases()
    {
        var requests = AgentToolProtocol.ParseRequests(
            """
            ```embodysense-tools
            [
              {"command":"list","targetPath":"workspace/shared"},
              {"tool":"search","path":"workspace/shared","query":"needle"}
            ]
            ```
            """);

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal(ToolCommand.List, request.Command);
                Assert.Equal("workspace/shared", request.TargetPath);
            },
            request =>
            {
                Assert.Equal(ToolCommand.Search, request.Command);
                Assert.Equal("workspace/shared", request.TargetPath);
                Assert.Equal("needle", request.Pattern);
            });
    }
}
