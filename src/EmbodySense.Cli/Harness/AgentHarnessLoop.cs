using EmbodySense.Cli.Common;
using EmbodySense.Core.Application.Harness;

namespace EmbodySense.Cli.Harness;

public static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(
        AgentHarnessSession session,
        HarnessCommandHandler? commandHandler = null,
        IHarnessTerminal? terminal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        terminal ??= ConsoleHarnessTerminal.Instance;
        commandHandler ??= new HarnessCommandHandler(terminal: terminal);
        terminal.WriteLine(Constants.HarnessBanner);
        var state = new HarnessLoopState();

        while (!state.ExitRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            terminal.Write("> ");
            var input = terminal.ReadLine();

            switch (input)
            {
                case null:
                    state.RequestExit();
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    if (await commandHandler.TryHandleAsync(input, session, state, cancellationToken))
                    {
                        break;
                    }

                    var wroteResponseChunk = false;
                    var responseEndedWithNewLine = false;
                    var response = await session.SendUserMessageAsync(input, (chunk, _) =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            terminal.Write(chunk);
                            wroteResponseChunk = true;
                            responseEndedWithNewLine = EndsWithNewLine(chunk);
                        }

                        return Task.CompletedTask;
                    }, cancellationToken);

                    if (!wroteResponseChunk)
                    {
                        terminal.WriteLine(response.OutputText);
                    }
                    else if (!responseEndedWithNewLine)
                    {
                        terminal.WriteLine();
                    }

                    state.MarkModelTurnStarted();
                    break;
            }
        }

        return 0;
    }

    private static bool EndsWithNewLine(string text)
    {
        return text.Length > 0 && text[^1] is '\n' or '\r';
    }
}
