using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Cli.Command.Models;

internal sealed record RunOptions(
    string? Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox)
{
    public static RunOptions FromArguments(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        RejectUnsupportedFlags(arguments, "--persist-session", "--approval", "--skip-git-repo-check");

        return new RunOptions(
            Model: arguments.OptionValueInTokenOrder("--model", "-m") ?? GetPositionalModel(arguments),
            WorkingDirectory: arguments.OptionValue("--workdir") ?? arguments.OptionValue("--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath: arguments.OptionValue("--codex-path"),
            CodexSandbox: arguments.OptionValue("--sandbox") ?? "read-only");
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
}
