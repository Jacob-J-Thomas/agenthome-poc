using System.ComponentModel;
using System.Diagnostics;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

internal sealed class CodexCliProcessRunner : ICodexCliProcessRunner
{
    public async Task<CodexCliProcessResult> RunAsync(ProcessStartInfo startInfo, string standardInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        using var process = StartCodex(startInfo);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken);

        return new CodexCliProcessResult(process.ExitCode, await outputTask, await errorTask);
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
