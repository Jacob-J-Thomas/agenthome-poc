using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Cli.Command;

public sealed class AgentRuntimeConsoleHost
{
    private const string UserPrompt = "User: ";
    private readonly AgentRuntime _runtime;
    private readonly IAgentRuntimeConsole _console;

    public AgentRuntimeConsoleHost(AgentRuntime runtime, IAgentRuntimeConsole console)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(console);

        _runtime = runtime;
        _console = console;
    }

    public async Task<int> RunAsync(
        string? banner = null,
        string prompt = UserPrompt,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(banner))
        {
            _console.WriteLine(banner);
        }

        if (verbose)
        {
            WriteCommandResult(_runtime.SetVerbose(true));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _console.Write(prompt);
            var input = _console.ReadLine();

            switch (input)
            {
                case null:
                    return 0;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    var result = await RunInputAsync(input, cancellationToken);
                    if (result.ExitRequested)
                    {
                        return 0;
                    }

                    break;
            }
        }
    }

    private async Task<AgentRuntimeTurnResult> RunInputAsync(string input, CancellationToken cancellationToken)
    {
        var wroteAssistantHeader = false;
        var wroteResponseChunk = false;
        var responseEndedWithNewLine = false;

        var result = await _runtime.RunTurnAsync(
            input,
            (chunk, _) =>
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    if (!wroteAssistantHeader)
                    {
                        _console.WriteLine(FormatMessageHeader("assistant"));
                        wroteAssistantHeader = true;
                    }

                    _console.Write(chunk);
                    wroteResponseChunk = true;
                    responseEndedWithNewLine = EndsWithNewLine(chunk);
                }

                return Task.CompletedTask;
            },
            (context, _) =>
            {
                _console.WriteLine(context);
                _console.WriteLine();
                return Task.CompletedTask;
            },
            cancellationToken);

        await WriteResultAsync(result, wroteResponseChunk, responseEndedWithNewLine, cancellationToken);
        return result;
    }

    private async Task WriteResultAsync(
        AgentRuntimeTurnResult result,
        bool wroteResponseChunk,
        bool responseEndedWithNewLine,
        CancellationToken cancellationToken)
    {
        if (result.IsMessageTurn)
        {
            WriteModelResult(result, wroteResponseChunk, responseEndedWithNewLine);
            return;
        }

        WriteCommandResult(result, wroteResponseChunk, responseEndedWithNewLine);
        if (!result.AwaitingInput)
        {
            return;
        }

        var answer = _console.ReadLine() ?? string.Empty;
        var answerResult = await _runtime.RunTurnAsync(answer, cancellationToken: cancellationToken);
        WriteCommandResult(answerResult);
    }

    private void WriteModelResult(AgentRuntimeTurnResult result, bool wroteResponseChunk, bool responseEndedWithNewLine)
    {
        var assistantMessage = result.Events.FirstOrDefault(turnEvent => turnEvent.Kind == AgentRuntimeTurnEventKind.AssistantMessage);
        if (!wroteResponseChunk)
        {
            _console.WriteLine(FormatMessageHeader("assistant"));
            _console.WriteLine(assistantMessage?.Text ?? result.Output);
        }
        else if (!responseEndedWithNewLine)
        {
            _console.WriteLine();
        }
    }

    private void WriteCommandResult(
        AgentRuntimeTurnResult result,
        bool wroteResponseChunk = false,
        bool responseEndedWithNewLine = true)
    {
        WriteTranscriptReplacement(result.Events.FirstOrDefault(turnEvent => turnEvent.Kind == AgentRuntimeTurnEventKind.TranscriptReplacement));

        foreach (var turnEvent in result.Events)
        {
            switch (turnEvent.Kind)
            {
                case AgentRuntimeTurnEventKind.TranscriptReplacement:
                    break;

                case AgentRuntimeTurnEventKind.CommandOutput:
                    _console.WriteLine(turnEvent.Text);
                    break;

                case AgentRuntimeTurnEventKind.Prompt:
                    _console.Write(turnEvent.Text + " ");
                    break;

                case AgentRuntimeTurnEventKind.AssistantMessage:
                    WriteAcceptedAssistantMessage(turnEvent.Text, wroteResponseChunk, responseEndedWithNewLine);
                    break;

                case AgentRuntimeTurnEventKind.Failure:
                case AgentRuntimeTurnEventKind.Cancellation:
                    _console.WriteLine(turnEvent.Text);
                    break;
            }
        }
    }

    private void WriteAcceptedAssistantMessage(string text, bool wroteResponseChunk, bool responseEndedWithNewLine)
    {
        if (!wroteResponseChunk)
        {
            _console.WriteLine(FormatMessageHeader("assistant"));
            _console.WriteLine(text);
        }
        else if (!responseEndedWithNewLine)
        {
            _console.WriteLine();
        }
    }

    private void WriteTranscriptReplacement(AgentRuntimeTurnEvent? turnEvent)
    {
        if (turnEvent is null)
        {
            return;
        }

        _console.Clear();
        _console.WriteLine(FormatRestoredConversation(turnEvent.TranscriptMessages));
        _console.WriteLine();
    }

    private static string FormatRestoredConversation(IReadOnlyList<AgentRuntimeTranscriptMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return "Loaded conversation transcript is empty.";
        }

        var lines = new List<string> { "Loaded conversation transcript:" };
        foreach (var message in messages)
        {
            lines.Add(FormatMessageHeader(message.Role));
            lines.Add(message.Content);
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static string FormatMessageHeader(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "system" => "System:",
            "user" => "User:",
            "assistant" => "Assistant:",
            "tool" => "Tool:",
            _ => role
        };
    }

    private static bool EndsWithNewLine(string text)
    {
        return text.Length > 0 && text[^1] is '\n' or '\r';
    }
}
