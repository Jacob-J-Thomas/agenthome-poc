using System.Globalization;
using System.Text;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Harness;

namespace EmbodySense.Cli.Harness.Tests;

public sealed class ConsoleHarnessAdapterTests
{
    [Fact]
    public async Task ConsoleToolApprovalPrompt_returns_approval_decisions_from_client_input()
    {
        var approvingClient = new ScriptedHarnessClient("yes");
        var rejectingClient = new ScriptedHarnessClient("no");
        var request = new ToolApprovalRequest(
            "request-1",
            new ToolRequest(ToolCommand.Write, ".agent/notes.md", "content"),
            "C:\\workspace\\.agent\\notes.md",
            FileSystemOperation.Modify,
            PermissionEvaluation.RequiresApproval("", "approval required"));

        var approved = await new ConsoleToolApprovalPrompt(approvingClient).RequestApprovalAsync(request);
        var rejected = await new ConsoleToolApprovalPrompt(rejectingClient).RequestApprovalAsync(request);

        Assert.True(approved.Approved);
        Assert.False(rejected.Approved);
        Assert.Contains("Tool approval required", approvingClient.Output, StringComparison.Ordinal);
        Assert.Contains("Matched:    (default policy)", approvingClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleHarnessTerminal_forwards_console_input_and_output()
    {
        var oldIn = Console.In;
        var oldOut = Console.Out;
        using var input = new StringReader("typed input");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);

            ConsoleHarnessTerminal.Instance.Write("hello");
            ConsoleHarnessTerminal.Instance.WriteLine(" world");
            var line = ConsoleHarnessTerminal.Instance.ReadLine();

            Assert.Equal("typed input", line);
            Assert.Equal("hello world" + Environment.NewLine, output.ToString());
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }
    }

    private sealed class ScriptedHarnessClient(params string[] inputs) : IHarnessClient
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
        }

        public void Write(string value)
        {
            _output.Append(value);
        }

        public void WriteLine(string value = "")
        {
            _output.AppendLine(value);
        }
    }
}
