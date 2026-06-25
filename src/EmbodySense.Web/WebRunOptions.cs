namespace EmbodySense.Web;

public sealed record WebRunOptions(
    string? Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox,
    string Host,
    int Port,
    bool PrintHelp)
{
    public const int DefaultPort = 4378;
    public const string DefaultHost = "127.0.0.1";
    private static readonly HashSet<string> LocalHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1" };

    public string Url => Host == "::1" ? $"http://[::1]:{Port}" : $"http://{Host}:{Port}";

    public static WebRunOptions FromArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var printHelp = args.Any(IsHelpToken);
        if (printHelp)
        {
            return new WebRunOptions(null, Directory.GetCurrentDirectory(), null, "read-only", DefaultHost, DefaultPort, true);
        }

        var host = OptionValue(args, "--host") ?? DefaultHost;
        if (!LocalHosts.Contains(host))
        {
            throw new ArgumentException("The web client only binds to localhost hosts: 127.0.0.1, localhost, or ::1.");
        }

        var portText = OptionValue(args, "--port");
        var port = string.IsNullOrWhiteSpace(portText) ? DefaultPort : ParsePort(portText);
        var sandbox = OptionValue(args, "--sandbox") ?? "read-only";
        ValidateSandbox(sandbox);

        return new WebRunOptions(
            Model: OptionValue(args, "--model") ?? OptionValue(args, "-m"),
            WorkingDirectory: OptionValue(args, "--workdir") ?? OptionValue(args, "--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath: OptionValue(args, "--codex-path"),
            CodexSandbox: sandbox,
            Host: host,
            Port: port,
            PrintHelp: printHelp);
    }

    private static string? OptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return RequireOptionValue(args, optionName, i);
            }
        }

        return null;
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be a number from 1 through 65535.");
        }

        return port;
    }

    private static bool IsHelpToken(string value)
    {
        return string.Equals(value, "help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireOptionValue(string[] args, string optionName, int optionIndex)
    {
        if (optionIndex + 1 >= args.Length || args[optionIndex + 1].StartsWith('-'))
        {
            throw new ArgumentException($"Option {optionName} requires a value.");
        }

        return args[optionIndex + 1];
    }

    private static void ValidateSandbox(string sandbox)
    {
        if (sandbox is not ("read-only" or "workspace-write" or "danger-full-access"))
        {
            throw new ArgumentException($"Unsupported sandbox mode: {sandbox}");
        }
    }
}
