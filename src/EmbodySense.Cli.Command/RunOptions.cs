using EmbodySense.Cli.Command;
namespace EmbodySense.Cli.Command;

/// <summary>
/// Represents validated options for one CLI run session.
/// </summary>
/// <param name="Model">The required explicitly selected model.</param>
/// <param name="WorkingDirectory">The workspace root used for runtime composition.</param>
/// <param name="CodexExecutablePath">The authoritative Codex executable path, or <see langword="null"/> for discovery.</param>
/// <param name="CodexSandbox">The Codex sandbox mode passed to app-server thread startup.</param>
/// <param name="Verbose">Whether startup context should be projected before ordinary turns.</param>
public sealed record RunOptions(
    string Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox,
    bool Verbose)
{
    /// <summary>
    /// Parses and validates the supported run options.
    /// </summary>
    /// <param name="arguments">The complete CLI token sequence.</param>
    /// <returns>Validated run options using current-directory and read-only sandbox defaults.</returns>
    /// <exception cref="ArgumentException">An option is missing a value or unsupported, the configured model is blank, or the sandbox value is unsupported.</exception>
    public static RunOptions FromArguments(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        RejectUnsupportedFlags(arguments, "--persist-session", "--approval", "--skip-git-repo-check");

        var model = arguments.OptionValueInTokenOrder("--model", "-m") ?? GetPositionalModel(arguments);
        var workingDirectory = arguments.OptionValue("--workdir") ?? arguments.OptionValue("--working-directory") ?? Directory.GetCurrentDirectory();
        var codexExecutablePath = arguments.OptionValue("--codex-path");
        var sandbox = arguments.OptionValue("--sandbox") ?? "read-only";
        ValidateSandbox(sandbox);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("CLI runtime composition requires a nonblank configured model.", nameof(arguments));
        }

        return new RunOptions(
            Model: model,
            WorkingDirectory: workingDirectory,
            CodexExecutablePath: codexExecutablePath,
            CodexSandbox: sandbox,
            Verbose: arguments.HasFlag("--verbose") || arguments.HasFlag("--verbose-context"));
    }

    private static string? GetPositionalModel(CliArguments arguments)
    {
        var value = arguments.At(1);
        return value is not null && !CliArguments.IsOption(value) ? value : null;
    }

    private static void RejectUnsupportedFlags(CliArguments arguments, params string[] unsupportedFlags)
    {
        foreach (var flag in unsupportedFlags)
        {
            if (arguments.HasFlag(flag))
            {
                throw new ArgumentException($"unsupported run option: {flag}");
            }
        }
    }

    private static void ValidateSandbox(string sandbox)
    {
        if (sandbox is not ("read-only" or "workspace-write" or "danger-full-access"))
        {
            throw new ArgumentException($"unsupported sandbox mode: {sandbox}");
        }
    }
}
