using System.Text.Json;
using EmbodySense.Cli.Audit;
using EmbodySense.Cli.Workspace;

namespace EmbodySense.Cli.Command;

internal static class AuditCommand
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
        var paths = new WorkspacePaths(root);
        var auditLog = new AuditLog(paths);
        var events = await auditLog.ReadTailAsync(limit);

        if (events.Count == 0)
        {
            Console.WriteLine($"No audit events found at {paths.EventsLogPath}");
            return 0;
        }

        foreach (var auditEvent in events)
        {
            PrintEvent(auditEvent);
        }

        return 0;
    }

    private static void PrintEvent(AuditEvent auditEvent)
    {
        var timestamp = auditEvent.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        Console.WriteLine($"{timestamp}  {auditEvent.Action,-24} {auditEvent.Outcome}");
        Console.WriteLine($"  target: {auditEvent.Target}");
        Console.WriteLine($"  detail: {auditEvent.Detail}");

        foreach (var item in auditEvent.Metadata.OrderBy(item => item.Key))
        {
            Console.WriteLine($"  {item.Key}: {FormatMetadataValue(item.Value)}");
        }

        Console.WriteLine();
    }

    private static string FormatMetadataValue(object? value)
    {
        return value switch
        {
            null => "",
            JsonElement element => FormatJsonElement(element),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => element.GetRawText()
        };
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
