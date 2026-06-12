using EmbodySense.Cli.Command;
using EmbodySense.Cli.Common;
using EmbodySense.Cli.Inference.Implementations;
using EmbodySense.Cli.Inference.Interfaces;
using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Harness;

internal static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(CliArguments arguments)
    {
        Console.WriteLine(Constants.HarnessBanner);

        var inferenceClient = CreateDefaultInferenceClient(arguments);
        var messages = new List<LlmMessage>();
        var exitRequested = false;

        while (!exitRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();

            switch (input)
            {
                case null:
                    exitRequested = true;
                    break;

                case var value when IsExitCommand(value):
                    exitRequested = true;
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    messages.Add(LlmMessage.User(input));

                    var response = await inferenceClient.GenerateAsync(new LlmInferenceRequest(messages));

                    messages.Add(LlmMessage.Assistant(response.OutputText));

                    Console.WriteLine(response.OutputText);
                    break;
            }
        }

        return 0;
    }

    private static ILlmInferenceClient CreateDefaultInferenceClient(CliArguments arguments)
    {
        return new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = arguments.OptionValueInTokenOrder("--model", "-m") ?? GetPositionalModel(arguments),
            WorkingDirectory = arguments.OptionValue("--workdir") ?? arguments.OptionValue("--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath = arguments.OptionValue("--codex-path"),
            CodexSandbox = arguments.OptionValue("--sandbox") ?? "read-only",
            CodexApprovalPolicy = arguments.OptionValue("--approval") ?? "never",
            UseEphemeralCodexSession = !arguments.HasFlag("--persist-session"),
            SkipCodexGitRepositoryCheck = arguments.HasFlag("--skip-git-repo-check")
        });
    }

    private static string? GetPositionalModel(CliArguments arguments)
    {
        var value = arguments.At(1);
        return value is not null && !CliArguments.IsOption(value) ? value : null;
    }

    private static bool IsExitCommand(string input)
    {
        return input.Trim().ToLowerInvariant() is "exit" or "quit" or "/exit" or "/quit";
    }
}
