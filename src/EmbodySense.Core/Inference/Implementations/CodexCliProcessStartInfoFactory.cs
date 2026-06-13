using System.Diagnostics;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

internal static class CodexCliProcessStartInfoFactory
{
    public static ProcessStartInfo Create(LlmInferenceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.CodexExecutablePath)
                ? GetDefaultCodexExecutable()
                : options.CodexExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        startInfo.ArgumentList.Add("--ask-for-approval");
        startInfo.ArgumentList.Add(options.CodexApprovalPolicy);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(options.CodexSandbox);

        if (options.UseEphemeralCodexSession)
        {
            startInfo.ArgumentList.Add("--ephemeral");
        }

        if (options.SkipCodexGitRepositoryCheck)
        {
            startInfo.ArgumentList.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(options.Model);
        }

        startInfo.ArgumentList.Add("-");

        return startInfo;
    }

    private static string GetDefaultCodexExecutable()
    {
        return OperatingSystem.IsWindows() ? "codex.cmd" : "codex";
    }
}
