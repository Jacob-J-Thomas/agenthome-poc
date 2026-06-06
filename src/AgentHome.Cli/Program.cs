
var exitCode = await AgentHomeCli.RunAsync(args);
return exitCode;

internal static class AgentHomeCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].Trim().ToLowerInvariant();

            return command switch
            {
                "init" => await InitAsync(args),
                //"status" => Status(args),
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

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AgentHome POC CLI

            usage:
              agenthome init [root]
              agenthome status [root]

            example:
              agenthome init ./scratch
            """);
    }

    private static async Task<int> InitAsync(string[] args)
    {
        return 0;
    }

    //private static int Status(string[] args)
    //{
    //    var root = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
    //    var paths = new WorkspacePaths(root);

    //    Console.WriteLine($"Root:          {paths.RootPath}");
    //    Console.WriteLine($"Agent path:    {paths.AgentPath}");
    //    Console.WriteLine($"Workspace:     {paths.WorkspacePath}");
    //    Console.WriteLine($"Initialized:   {paths.IsInitialized}");
    //    Console.WriteLine($"Audit log:     {paths.EventsLogPath}");
    //    Console.WriteLine($"Tasks path:    {paths.TasksPath}");

    //    return paths.IsInitialized ? 0 : 2;
    //}


    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        PrintHelp();
        return 1;
    }
}
