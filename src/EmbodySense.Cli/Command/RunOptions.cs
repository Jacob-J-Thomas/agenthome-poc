using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Cli.Command;

internal sealed record RunOptions(
    string? Model,
    string WorkingDirectory,
    string? CodexExecutablePath,
    string CodexSandbox,
    string CodexApprovalPolicy,
    bool UseEphemeralCodexSession,
    bool SkipCodexGitRepositoryCheck)
{
    public static RunOptions FromArguments(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return new RunOptions(
            Model: arguments.OptionValueInTokenOrder("--model", "-m") ?? GetPositionalModel(arguments),
            WorkingDirectory: arguments.OptionValue("--workdir") ?? arguments.OptionValue("--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath: arguments.OptionValue("--codex-path"),
            CodexSandbox: arguments.OptionValue("--sandbox") ?? "read-only",
            CodexApprovalPolicy: arguments.OptionValue("--approval") ?? "never",
            UseEphemeralCodexSession: !arguments.HasFlag("--persist-session"),
            SkipCodexGitRepositoryCheck: arguments.HasFlag("--skip-git-repo-check"));
    }

    public LlmInferenceClientOptions ToInferenceClientOptions()
    {
        return new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = Model,
            WorkingDirectory = WorkingDirectory,
            CodexExecutablePath = CodexExecutablePath,
            CodexSandbox = CodexSandbox,
            CodexApprovalPolicy = CodexApprovalPolicy,
            UseEphemeralCodexSession = UseEphemeralCodexSession,
            SkipCodexGitRepositoryCheck = SkipCodexGitRepositoryCheck
        };
    }

    private static string? GetPositionalModel(CliArguments arguments)
    {
        var value = arguments.At(1);
        return value is not null && !CliArguments.IsOption(value) ? value : null;
    }
}
