using EmbodySense.Cli.Command;
namespace EmbodySense.Cli.Command;

/// <summary>
/// Represents validated options for one CLI run session.
/// </summary>
/// <param name="Model">The explicitly selected model, or <see langword="null"/> to use external configuration.</param>
/// <param name="WorkingDirectory">The workspace root used for runtime composition.</param>
/// <param name="CodexExecutablePath">The authoritative Codex executable path, or <see langword="null"/> for discovery.</param>
/// <param name="CodexSandbox">The Codex sandbox mode passed to app-server thread startup.</param>
/// <param name="Verbose">Whether startup context should be projected before ordinary turns.</param>
public sealed record RunOptions(
    string? Model,
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
    /// <exception cref="ArgumentException">An option is missing a value, unsupported, or has an unsupported sandbox value.</exception>
    public static RunOptions FromArguments(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        RejectUnsupportedFlags(arguments, "--persist-session", "--approval", "--skip-git-repo-check");

        var sandbox = arguments.OptionValue("--sandbox") ?? "read-only";
        ValidateSandbox(sandbox);

        return new RunOptions(
            Model: arguments.OptionValueInTokenOrder("--model", "-m") ?? GetPositionalModel(arguments),
            WorkingDirectory: arguments.OptionValue("--workdir") ?? arguments.OptionValue("--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath: arguments.OptionValue("--codex-path"),
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
