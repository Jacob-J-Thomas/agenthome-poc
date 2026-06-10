using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EmbodySense.Cli.Common.Enums;
using EmbodySense.Cli.Inference.Interfaces;
using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Inference.Implementations;

internal sealed class CodexCliInferenceClient : ILlmInferenceClient
{
    private readonly LlmInferenceClientOptions _options;

    public CodexCliInferenceClient(LlmInferenceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = BuildPrompt(request);
        var startInfo = CreateStartInfo();

        using var process = StartCodex(startInfo);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(process.ExitCode, output, error));
        }

        return new LlmInferenceResponse(
            output.TrimEnd(),
            LlmInferenceSurface.OpenAiCodex,
            _options.Model);
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(_options.CodexExecutablePath)
                ? GetDefaultCodexExecutable()
                : _options.CodexExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = _options.WorkingDirectory;
        }

        startInfo.ArgumentList.Add("--ask-for-approval");
        startInfo.ArgumentList.Add(_options.CodexApprovalPolicy);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(_options.CodexSandbox);

        if (_options.UseEphemeralCodexSession)
        {
            startInfo.ArgumentList.Add("--ephemeral");
        }

        if (_options.SkipCodexGitRepositoryCheck)
        {
            startInfo.ArgumentList.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_options.Model);
        }

        startInfo.ArgumentList.Add("-");

        return startInfo;
    }

    private static Process StartCodex(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex CLI did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Codex CLI was not found. Install Codex and run `codex login` with ChatGPT to use Codex subscription-backed inferencing.",
                exception);
        }
    }

    private static string GetDefaultCodexExecutable()
    {
        return OperatingSystem.IsWindows() ? "codex.cmd" : "codex";
    }

    private static string BuildPrompt(LlmInferenceRequest request)
    {
        var builder = new StringBuilder();

        foreach (var message in request.Messages)
        {
            builder.Append(GetRoleLabel(message.Role));
            builder.AppendLine(":");
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetRoleLabel(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "System",
            LlmMessageRole.User => "User",
            LlmMessageRole.Assistant => "Assistant",
            _ => "Message"
        };
    }

    private static string CreateFailureMessage(int exitCode, string output, string error)
    {
        var details = string.IsNullOrWhiteSpace(error) ? output : error;

        return string.IsNullOrWhiteSpace(details)
            ? $"Codex CLI exited with code {exitCode}."
            : $"Codex CLI exited with code {exitCode}: {details.Trim()}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
