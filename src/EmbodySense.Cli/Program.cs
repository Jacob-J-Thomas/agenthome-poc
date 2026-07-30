using EmbodySense.Cli.Command;

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
            HelpCommand.PrintRoot();
            return 0;
        }

        try
        {
            var command = arguments.Command ?? "";

            return command switch
            {
                "init" => await InitCommand.RunAsync(arguments),
                "status" => StatusCommand.Run(arguments),
                "run" => await RunCommand.RunAsync(arguments),
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
        HelpCommand.PrintRoot();
        return 1;
    }
}
