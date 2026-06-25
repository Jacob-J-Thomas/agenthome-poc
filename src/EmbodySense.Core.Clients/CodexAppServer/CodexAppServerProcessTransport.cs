using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

[ExcludeFromCodeCoverage]
internal sealed class CodexAppServerProcessTransport : ICodexAppServerTransport
{
    private const int MaxErrorOutputCharacters = 32_000;
    private readonly Process _process;
    private readonly Task _errorReaderTask;
    private readonly StringBuilder _errorOutput = new();

    public CodexAppServerProcessTransport(LlmInferenceClientOptions options, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _process = StartCodex(CreateStartInfo(options, workingDirectory));
        _errorReaderTask = ReadErrorOutputAsync();
    }

    public string ErrorOutput
    {
        get
        {
            lock (_errorOutput)
            {
                return _errorOutput.ToString();
            }
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return await _process.StandardOutput.ReadLineAsync(cancellationToken);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        TryKill(_process);

        try
        {
            await _errorReaderTask;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(LlmInferenceClientOptions options, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.CodexExecutablePath)
                ? GetDefaultCodexExecutable()
                : options.CodexExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        return startInfo;
    }

    private async Task ReadErrorOutputAsync()
    {
        while (!_process.HasExited)
        {
            var line = await _process.StandardError.ReadLineAsync();

            if (line is null)
            {
                return;
            }

            lock (_errorOutput)
            {
                _errorOutput.AppendLine(line);
                if (_errorOutput.Length > MaxErrorOutputCharacters)
                {
                    const string marker = "[stderr truncated]";
                    var keepCharacters = MaxErrorOutputCharacters - marker.Length - Environment.NewLine.Length;
                    _errorOutput.Remove(0, _errorOutput.Length - keepCharacters);
                    _errorOutput.Insert(0, marker + Environment.NewLine);
                }
            }
        }
    }

    private static Process StartCodex(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex app-server did not start.");
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
