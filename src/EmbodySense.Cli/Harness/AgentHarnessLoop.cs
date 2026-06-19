using EmbodySense.Cli.Common;
using EmbodySense.Core.Harness;

namespace EmbodySense.Cli.Harness;

internal static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(
        AgentHarnessSession session,
        HarnessCommandHandler? commandHandler = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        commandHandler ??= new HarnessCommandHandler();
        Console.WriteLine(Constants.HarnessBanner);
        var exitRequested = false;
        var modelTurnStarted = false;

        while (!exitRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();

            switch (input)
            {
                case null:
                    exitRequested = true;
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    var commandResult = await commandHandler.TryHandleAsync(input, session, modelTurnStarted);
                    if (commandResult == HarnessCommandResult.ExitRequested)
                    {
                        exitRequested = true;
                        break;
                    }

                    if (commandResult == HarnessCommandResult.Handled)
                    {
                        break;
                    }

                    if (commandResult == HarnessCommandResult.NewSessionStarted)
                    {
                        modelTurnStarted = false;
                        break;
                    }

                    var wroteResponseChunk = false;
                    var responseEndedWithNewLine = false;
                    var response = await session.SendUserMessageAsync(input, (chunk, _) =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            Console.Write(chunk);
                            wroteResponseChunk = true;
                            responseEndedWithNewLine = EndsWithNewLine(chunk);
                        }

                        return Task.CompletedTask;
                    });

                    if (!wroteResponseChunk)
                    {
                        Console.WriteLine(response.OutputText);
                    }
                    else if (!responseEndedWithNewLine)
                    {
                        Console.WriteLine();
                    }

                    modelTurnStarted = true;
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
