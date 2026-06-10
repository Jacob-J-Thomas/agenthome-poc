using EmbodySense.Cli.Audit;
using EmbodySense.Cli.Common;
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
        if (args.Length == 0 || IsHelp(args[0]))
        {
            CliHelpers.PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].Trim().ToLowerInvariant();

            return command switch
            {
                "init" => await CliHelpers.InitAsync(args),
                "status" => CliHelpers.Status(args),
                "run" => await AgentHarnessLoop.RunHarnessLoopAsync(args),
                "audit" => await AuditCommand.RunAsync(args),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string value)
    {
        return value is "help" or "--help" or "-h";
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        CliHelpers.PrintHelp();
        return 1;
    }
}
