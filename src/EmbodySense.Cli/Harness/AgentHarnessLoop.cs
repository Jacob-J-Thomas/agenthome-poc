using EmbodySense.Cli.Common;
using EmbodySense.Cli.Common.Enums;
using EmbodySense.Cli.Inference.Implementations;
using EmbodySense.Cli.Inference.Interfaces;
using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Harness;

internal static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(string[] args)
    {
        Console.WriteLine(Constants.HarnessBanner);

        var inferenceClient = CreateDefaultInferenceClient(args);
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

    private static ILlmInferenceClient CreateDefaultInferenceClient(string[] args)
    {
        return new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = GetModel(args),
            WorkingDirectory = GetOptionValue(args, "--workdir") ?? GetOptionValue(args, "--working-directory") ?? Directory.GetCurrentDirectory(),
            CodexExecutablePath = GetOptionValue(args, "--codex-path"),
            CodexSandbox = GetOptionValue(args, "--sandbox") ?? "read-only",
            CodexApprovalPolicy = GetOptionValue(args, "--approval") ?? "never",
            UseEphemeralCodexSession = !HasFlag(args, "--persist-session"),
            SkipCodexGitRepositoryCheck = HasFlag(args, "--skip-git-repo-check")
        });
    }

    private static string? GetModel(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] is "--model" or "-m") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return args.Length >= 2 && !args[1].StartsWith('-') ? args[1] : null;
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

    private static bool HasFlag(string[] args, string flagName)
    {
        return args.Skip(1).Any(arg => arg.Equals(flagName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExitCommand(string input)
    {
        return input.Trim().ToLowerInvariant() is "exit" or "quit" or "/exit" or "/quit";
    }
}
