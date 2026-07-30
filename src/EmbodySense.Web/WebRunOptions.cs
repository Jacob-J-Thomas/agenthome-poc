using EmbodySense.Web.Models;
namespace EmbodySense.Web;

/// <summary>
/// Represents validated process options for one localhost Web host.
/// </summary>
/// <param name="Model">The explicit model, or <see langword="null"/> for external configuration.</param>
/// <param name="WorkingDirectory">The workspace root used by runtime, authoring, audit, and persistence services.</param>
/// <param name="CodexExecutablePath">The authoritative Codex executable path, or <see langword="null"/> for discovery.</param>
/// <param name="CodexSandbox">The sandbox mode passed to Codex app-server thread startup.</param>
/// <param name="Host">One accepted loopback host spelling.</param>
/// <param name="Port">The local TCP port from 1 through 65535.</param>
/// <param name="PrintHelp">Whether startup should print usage without validating or starting other options.</param>
public sealed record WebRunOptions(
    string? Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox,
    string Host,
    int Port,
    bool PrintHelp)
{
    /// <summary>
    /// Gets the default localhost port.
    /// </summary>
    public const int DefaultPort = 4378;

    /// <summary>
    /// Gets the default IPv4 loopback bind host.
    /// </summary>
    public const string DefaultHost = "127.0.0.1";
    private static readonly HashSet<string> _localHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1" };

    /// <summary>
    /// Gets the HTTP origin, including IPv6 brackets when required.
    /// </summary>
    public string Url => Host == "::1" ? $"http://[::1]:{Port}" : $"http://{Host}:{Port}";

    /// <summary>
    /// Parses and validates the supported Web host options.
    /// </summary>
    /// <param name="args">The command-line tokens.</param>
    /// <returns>Validated options using current-directory, localhost, port 4378, and read-only defaults.</returns>
    /// <exception cref="ArgumentException">
    /// An option lacks a value, the host is not loopback, the port is out of range, or the sandbox is unsupported.
    /// </exception>
    /// <remarks>Any help token short-circuits other parsing so usage remains available despite unrelated invalid options.</remarks>
    public static WebRunOptions FromArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var printHelp = args.Any(IsHelpToken);
        if (printHelp)
        {
            return new WebRunOptions(null, Directory.GetCurrentDirectory(), null, "read-only", DefaultHost, DefaultPort, true);
        }

        var host = OptionValue(args, "--host") ?? DefaultHost;
        if (!_localHosts.Contains(host))
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
