using System.Text;
using EmbodySense.Cli.Harness;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Tests.Cli.Harness;

public sealed class AgentHarnessLoopTests
{
    [Fact]
    public async Task RunHarnessLoopAsync_handles_harness_commands_without_model_inference()
    {
        var terminal = new ScriptedHarnessTerminal("/help", "/exit");
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        Assert.Empty(client.Requests);
        Assert.Contains("Harness commands:", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("/new, /new-session", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHarnessLoopAsync_streams_unhandled_input_through_session()
    {
        var terminal = new ScriptedHarnessTerminal("hello", "/exit");
        var client = new ScriptedInferenceClient("hello world");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        var requestMessages = Assert.Single(client.Requests);
        Assert.Collection(requestMessages, message => Assert.Equal("hello", message.Content));
        Assert.Contains("hello world", terminal.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(terminal.Output, "hello world"));
    }

    [Fact]
    public async Task HarnessCommandHandler_new_session_resets_model_turn_state()
    {
        var terminal = new ScriptedHarnessTerminal();
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client, initialMessages: [LlmMessage.User("old prompt")]);
        var commandHandler = new HarnessCommandHandler(startupMessages: [LlmMessage.System("startup")], terminal: terminal);
        var state = new HarnessLoopState();
        state.MarkModelTurnStarted();

        var handled = await commandHandler.TryHandleAsync("/new", session, state);

        Assert.True(handled);
        Assert.False(state.ModelTurnStarted);
        var message = Assert.Single(session.Messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Equal("startup", message.Content);
        Assert.Contains("Started a new conversation.", terminal.Output, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        return text.Split(value, StringSplitOptions.None).Length - 1;
    }

    private sealed class ScriptedHarnessTerminal(params string[] inputs) : IHarnessTerminal
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

    private sealed class ScriptedInferenceClient(params string[] outputs) : ILlmInferenceClient
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            var output = _outputs.Dequeue();

            if (responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }
}
