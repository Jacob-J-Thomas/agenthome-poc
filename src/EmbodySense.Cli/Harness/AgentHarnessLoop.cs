using EmbodySense.Cli.Common;
using EmbodySense.Core.Harness;

namespace EmbodySense.Cli.Harness;

internal static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(AgentHarnessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Console.WriteLine(Constants.HarnessBanner);
        var exitRequested = false;

        while (!exitRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();

            switch (input)
            {
                case null:
                    exitRequested = true;
                    break;

                case var value when IsExitCommand(value):
                    exitRequested = true;
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    var response = await session.SendUserMessageAsync(input);
                    Console.WriteLine(response.OutputText);
                    break;
            }
        }

        return 0;
    }

    private static bool IsExitCommand(string input)
    {
        return input.Trim().ToLowerInvariant() is "exit" or "quit" or "/exit" or "/quit";
    }
}
