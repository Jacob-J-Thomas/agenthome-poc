using EmbodySense.Core.Application.Inference.Models;

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

        return new WebRunOptions(
            Model: OptionValue(args, "--model") ?? OptionValue(args, "-m"),
            WorkingDirectory: OptionValue(args, "--workdir") ?? OptionValue(args, "--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath: OptionValue(args, "--codex-path"),
            CodexSandbox: OptionValue(args, "--sandbox") ?? "read-only",
            Host: host,
            Port: port,
            PrintHelp: printHelp);
    }

    public LlmInferenceClientOptions ToInferenceClientOptions()
    {
        return new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = Model,
            WorkingDirectory = WorkingDirectory,
            CodexExecutablePath = CodexExecutablePath,
            CodexSandbox = CodexSandbox
        };
    }

    private static string? OptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
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
}
