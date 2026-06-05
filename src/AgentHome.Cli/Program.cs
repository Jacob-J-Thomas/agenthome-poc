using AgentHome.Core.Audit;
using AgentHome.Core.Context;
using AgentHome.Core.Policy;
using AgentHome.Core.Tasks;
using AgentHome.Core.Workspace;

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
                "status" => Status(args),
                "task" => await TaskAsync(args),
                "policy" => await PolicyAsync(args),
                "audit" => await AuditAsync(args),
                "context" => await ContextAsync(args),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> InitAsync(string[] args)
    {
        var root = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
        var initializer = new WorkspaceInitializer();
        await initializer.InitializeAsync(root);
        Console.WriteLine($"Initialized AgentHome workspace at {Path.GetFullPath(root)}");
        return 0;
    }

    private static int Status(string[] args)
    {
        var root = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
        var paths = new WorkspacePaths(root);

        Console.WriteLine($"Root:          {paths.RootPath}");
        Console.WriteLine($"Agent path:    {paths.AgentPath}");
        Console.WriteLine($"Workspace:     {paths.WorkspacePath}");
        Console.WriteLine($"Initialized:   {paths.IsInitialized}");
        Console.WriteLine($"Audit log:     {paths.EventsLogPath}");
        Console.WriteLine($"Tasks path:    {paths.TasksPath}");

        return paths.IsInitialized ? 0 : 2;
    }

    private static async Task<int> TaskAsync(string[] args)
    {
        if (args.Length < 3 || !args[1].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: agenthome task start \"goal\" [root]");
            return 1;
        }

        var goal = args[2];
        var root = args.Length >= 4 ? args[3] : Directory.GetCurrentDirectory();
        var paths = RequireInitialized(root);
        var store = new TaskStore(paths);
        var task = await store.StartAsync(goal);

        Console.WriteLine($"Started task: {task.Id}");
        Console.WriteLine($"Goal: {task.Goal}");
        return 0;
    }

    private static async Task<int> PolicyAsync(string[] args)
    {
        if (args.Length < 4 || !args[1].Equals("check", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: agenthome policy check <action> <target> [root]");
            return 1;
        }

        var action = args[2];
        var target = args[3];
        var root = args.Length >= 5 ? args[4] : Directory.GetCurrentDirectory();
        var paths = RequireInitialized(root);
        var engine = new PolicyEngine(paths);
        var evaluation = await engine.EvaluateAsync(action, target);

        Console.WriteLine($"Decision: {evaluation.Decision}");
        Console.WriteLine($"Action:   {evaluation.Action}");
        Console.WriteLine($"Target:   {evaluation.Target}");
        Console.WriteLine($"Source:   {evaluation.Source}");

        if (!string.IsNullOrWhiteSpace(evaluation.Reason))
        {
            Console.WriteLine($"Reason:   {evaluation.Reason}");
        }

        return evaluation.Decision switch
        {
            PermissionDecision.Allow => 0,
            PermissionDecision.Prompt => 3,
            PermissionDecision.Deny => 4,
            _ => 1
        };
    }

    private static async Task<int> AuditAsync(string[] args)
    {
        var count = 20;
        var rootIndex = 1;

        if (args.Length >= 2 && int.TryParse(args[1], out var parsedCount))
        {
            count = parsedCount;
            rootIndex = 2;
        }

        var root = args.Length > rootIndex ? args[rootIndex] : Directory.GetCurrentDirectory();
        var paths = RequireInitialized(root);
        var audit = new AuditLog(paths);
        var lines = await audit.ReadTailAsync(count);

        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static async Task<int> ContextAsync(string[] args)
    {
        if (args.Length < 3 || !args[1].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: agenthome context export codex [root]");
            return 1;
        }

        var target = args[2].Trim().ToLowerInvariant();
        if (target != "codex")
        {
            Console.Error.WriteLine("Only `codex` context export is implemented in the POC.");
            return 1;
        }

        var root = args.Length >= 4 ? args[3] : Directory.GetCurrentDirectory();
        var paths = RequireInitialized(root);
        var exporter = new ContextExporter(paths);
        var outputPath = await exporter.ExportCodexAsync();

        Console.WriteLine($"Exported Codex context: {outputPath}");
        return 0;
    }

    private static WorkspacePaths RequireInitialized(string root)
    {
        var paths = new WorkspacePaths(root);
        if (!paths.IsInitialized)
        {
            throw new InvalidOperationException($"No AgentHome workspace found at {paths.RootPath}. Run `agenthome init` first.");
        }

        return paths;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        PrintHelp();
        return 1;
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
  agenthome task start "goal" [root]
  agenthome policy check <action> <target> [root]
  agenthome audit [count] [root]
  agenthome context export codex [root]

examples:
  agenthome init ./scratch
  agenthome task start "Refactor authentication middleware" ./scratch
  agenthome policy check file.write workspace/shared/demo.txt ./scratch
  agenthome audit 20 ./scratch
""");
    }
}
