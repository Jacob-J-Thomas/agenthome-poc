using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Startup.Audit;

namespace EmbodySense.Cli.Command;

public static class AuditCommand
{
    private static readonly IReadOnlySet<string> IgnoredRootTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tail" };
    private static readonly IReadOnlySet<string> OptionsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--limit", "-n" };

    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var subcommand = arguments.At(1);

        if (arguments.Count >= 2 && arguments.IsHelpAt(1))
        {
            PrintHelp();
            return 0;
        }

        if (subcommand is not null && !IsTail(subcommand) && CliArguments.IsOption(subcommand))
        {
            return await PrintTailAsync(arguments);
        }

        if (subcommand is not null && !IsTail(subcommand) && IsReservedSubcommand(subcommand))
        {
            Console.Error.WriteLine($"unknown audit command: {subcommand}");
            PrintHelp();
            return 1;
        }

        return await PrintTailAsync(arguments);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            EmbodySense audit commands

            usage:
              embodysense audit [root] [--limit count]
              embodysense audit tail [root] [--limit count]

            example:
              embodysense audit tail ./scratch --limit 10
            """);
    }

    private static async Task<int> PrintTailAsync(CliArguments arguments)
    {
        var root = GetRoot(arguments);
        var limit = GetLimit(arguments);
        var tail = await new AuditTailReader().ReadTailAsync(root, limit);

        if (tail.Events.Count == 0)
        {
            Console.WriteLine($"No audit events found at {tail.EventsLogPath}");
            return 0;
        }

        foreach (var auditEvent in tail.Events)
        {
            PrintEvent(auditEvent);
        }

        return 0;
    }

    private static void PrintEvent(AuditTailEvent auditEvent)
    {
        var timestamp = auditEvent.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        Console.WriteLine($"{timestamp}  {auditEvent.Action,-24} {auditEvent.Outcome}");
        Console.WriteLine($"  target: {auditEvent.Target}");
        Console.WriteLine($"  detail: {auditEvent.Detail}");

        foreach (var item in auditEvent.Metadata.OrderBy(item => item.Key))
        {
            Console.WriteLine($"  {item.Key}: {item.Value}");
        }

        Console.WriteLine();
    }

    private static string GetRoot(CliArguments arguments)
    {
        return arguments.FirstOperand(1, IgnoredRootTokens, OptionsWithValues) ?? Directory.GetCurrentDirectory();
    }

    private static int GetLimit(CliArguments arguments)
    {
        var value = arguments.OptionValue("--limit") ?? arguments.OptionValue("-n");
        return int.TryParse(value, out var limit) && limit > 0 ? limit : 20;
    }

    private static bool IsTail(string value)
    {
        return value.Equals("tail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedSubcommand(string value)
    {
        return value.Equals("summary", StringComparison.OrdinalIgnoreCase) || value.Equals("show", StringComparison.OrdinalIgnoreCase);
    }
}
