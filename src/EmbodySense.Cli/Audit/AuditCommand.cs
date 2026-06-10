using System.Text.Json;
using EmbodySense.Cli.Workspace;

namespace EmbodySense.Cli.Audit;

internal static class AuditCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 2 && IsHelp(args[1]))
        {
            PrintHelp();
            return 0;
        }

        if (args.Length >= 2 && !IsTail(args[1]) && args[1].StartsWith('-'))
        {
            return await PrintTailAsync(args);
        }

        if (args.Length >= 2 && !IsTail(args[1]) && IsReservedSubcommand(args[1]))
        {
            Console.Error.WriteLine($"unknown audit command: {args[1]}");
            PrintHelp();
            return 1;
        }

        return await PrintTailAsync(args);
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

    private static async Task<int> PrintTailAsync(string[] args)
    {
        var root = GetRoot(args);
        var limit = GetLimit(args);
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

    private static string GetRoot(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (IsTail(args[i]))
            {
                continue;
            }

            if (IsOptionWithValue(args[i]))
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static int GetLimit(string[] args)
    {
        var value = GetOptionValue(args, "--limit") ?? GetOptionValue(args, "-n");
        return int.TryParse(value, out var limit) && limit > 0 ? limit : 20;
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool IsHelp(string value)
    {
        return value is "help" or "--help" or "-h";
    }

    private static bool IsTail(string value)
    {
        return value.Equals("tail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedSubcommand(string value)
    {
        return value.Equals("summary", StringComparison.OrdinalIgnoreCase) || value.Equals("show", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptionWithValue(string value)
    {
        return value is "--limit" or "-n";
    }
}
