using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Hosts one interactive CLI conversation over a shared <see cref="AgentRuntime"/>.
/// </summary>
/// <remarks>
/// The host owns console projection and serializes all runtime turns until the user requests exit,
/// the input stream closes, or cancellation is observed before a top-level prompt read or during a turn.
/// Synchronous console input, including a command's follow-up prompt, cannot be interrupted by the token.
/// The host does not own or dispose the supplied runtime.
/// </remarks>
public sealed class AgentRuntimeConsoleHost
{
    private const string UserPrompt = "User: ";
    private readonly AgentRuntime _runtime;
    private readonly IAgentRuntimeConsole _console;

    /// <summary>
    /// Initializes a console host for an already composed runtime.
    /// </summary>
    /// <param name="runtime">The session runtime that processes commands and model turns.</param>
    /// <param name="console">The console abstraction used for all interactive input and output.</param>
    public AgentRuntimeConsoleHost(AgentRuntime runtime, IAgentRuntimeConsole console)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(console);

        _runtime = runtime;
        _console = console;
    }

    /// <summary>
    /// Runs the interactive console loop until end-of-input or an accepted exit command.
    /// </summary>
    /// <param name="banner">Optional text written once before the first prompt.</param>
    /// <param name="prompt">The prompt written before each input read.</param>
    /// <param name="verbose">Whether to enable verbose runtime context before accepting input.</param>
    /// <param name="cancellationToken">The token checked before each top-level prompt read and passed through runtime turns; it cannot interrupt any synchronous input read and is not rechecked before a command follow-up read.</param>
    /// <returns>Zero after an orderly exit or end-of-input.</returns>
    /// <exception cref="OperationCanceledException">The token is observed as cancelled before a top-level prompt read or during a runtime turn.</exception>
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
