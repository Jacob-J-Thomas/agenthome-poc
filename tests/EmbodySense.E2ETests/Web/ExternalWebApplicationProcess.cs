using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using EmbodySense.Web.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class ExternalWebApplicationProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process _process;
    private readonly ProcessOutputBuffer _output;
    private readonly ProcessOutputBuffer _error;

    private ExternalWebApplicationProcess(Process process, ProcessOutputBuffer output, ProcessOutputBuffer error, string baseUrl)
    {
        _process = process;
        _output = output;
        _error = error;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<ExternalWebApplicationProcess> StartAsync(string workspaceRoot, int port, string codexExecutablePath, string model)
    {
        var webAssemblyPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.Web.dll");
        if (!File.Exists(webAssemblyPath))
        {
            throw new InvalidOperationException($"Expected Web assembly at {webAssemblyPath}.");
        }

        var output = new ProcessOutputBuffer();
        var error = new ProcessOutputBuffer();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(webAssemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                webAssemblyPath,
                "--workdir",
                workspaceRoot,
                "--port",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--model",
                model,
                "--codex-path",
                codexExecutablePath
            }
        }) ?? throw new InvalidOperationException("External Web process did not start.");
        process.OutputDataReceived += (_, args) => output.Append(args.Data);
        process.ErrorDataReceived += (_, args) => error.Append(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var application = new ExternalWebApplicationProcess(process, output, error, $"http://127.0.0.1:{port}");
        try
        {
            await application.WaitUntilReadyAsync();
            return application;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public async Task WriteDiagnosticsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "server-output.txt"), FormatOutput());
    }

    public string FormatOutput()
    {
        return "server stdout:" + Environment.NewLine + _output.Text + Environment.NewLine + "server stderr:" + Environment.NewLine + _error.Text;
    }

    public void AssertHealthy()
    {
        Assert.False(_process.HasExited, $"External Web process exited unexpectedly.{Environment.NewLine}{FormatOutput()}");
        Assert.True(string.IsNullOrWhiteSpace(_error.Text), $"External Web process wrote to stderr.{Environment.NewLine}{FormatOutput()}");
        Assert.DoesNotContain("fail:", _output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", _output.Text, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private async Task WaitUntilReadyAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(2) };
        Exception? lastException = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"External Web process exited with code {_process.ExitCode}.{Environment.NewLine}{FormatOutput()}");
            }

            try
            {
                var status = await client.GetFromJsonAsync<WebStatus>("/api/status", _jsonOptions);
                if (status is not null)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastException = exception;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"External Web process did not serve /api/status.{Environment.NewLine}{FormatOutput()}", lastException);
    }
}
