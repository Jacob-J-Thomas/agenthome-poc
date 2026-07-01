using System.Globalization;
using System.Text;
using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Cli.Command.Tests;

public sealed class ConsoleRuntimeAdapterTests
{
    [Fact]
    public async Task ConsoleToolApprovalPrompt_returns_approval_decisions_from_client_input()
    {
        var approvingClient = new ScriptedRuntimeClient("yes");
        var rejectingClient = new ScriptedRuntimeClient("no");
        var request = new AgentToolApprovalRequest(
            "request-1",
            "write",
            ".agent/notes.md",
            "C:\\workspace\\.agent\\notes.md",
            "modify",
            "(default policy)",
            "approval required");

        var approved = await new ConsoleToolApprovalPrompt(approvingClient).RequestApprovalAsync(request);
        var rejected = await new ConsoleToolApprovalPrompt(rejectingClient).RequestApprovalAsync(request);

        Assert.True(approved.Approved);
        Assert.False(rejected.Approved);
        Assert.Contains("Tool approval required", approvingClient.Output, StringComparison.Ordinal);
        Assert.Contains("Matched:    (default policy)", approvingClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleRuntimeTerminal_forwards_console_input_and_output()
    {
        var oldIn = Console.In;
        var oldOut = Console.Out;
        using var input = new StringReader("typed input");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);

            ConsoleRuntimeTerminal.Instance.Write("hello");
            ConsoleRuntimeTerminal.Instance.WriteLine(" world");
            var line = ConsoleRuntimeTerminal.Instance.ReadLine();

            Assert.Equal("typed input", line);
            Assert.Equal("hello world" + Environment.NewLine, output.ToString());
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }
    }

    private sealed class ScriptedRuntimeClient(params string[] inputs) : IAgentRuntimeConsole
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
        }

        public void Clear()
        {
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
