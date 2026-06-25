namespace EmbodySense.Cli.Command.Models;

public sealed record RunOptions(
    string? Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox)
{
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
            CodexSandbox: sandbox);
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
