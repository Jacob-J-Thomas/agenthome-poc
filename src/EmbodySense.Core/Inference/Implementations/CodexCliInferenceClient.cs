using System.ComponentModel;
using System.Diagnostics;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

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

        var prompt = CodexCliPromptFormatter.Format(request);
        var startInfo = CodexCliProcessStartInfoFactory.Create(_options);

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
