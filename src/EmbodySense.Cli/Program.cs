using EmbodySense.Cli.Audit;
using EmbodySense.Cli.Command;
using EmbodySense.Cli.Harness;

namespace EmbodySense.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        return RunAsync(args);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var arguments = new CliArguments(args);

        if (arguments.Count == 0 || arguments.IsHelpAt(0))
        {
            CliHelpers.PrintHelp();
            return 0;
        }

        try
        {
            var command = arguments.Command ?? "";

            return command switch
            {
                "init" => await CliHelpers.InitAsync(arguments),
                "status" => CliHelpers.Status(arguments),
                "run" => await AgentHarnessLoop.RunHarnessLoopAsync(arguments),
                "audit" => await AuditCommand.RunAsync(arguments),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        CliHelpers.PrintHelp();
        return 1;
    }
}
