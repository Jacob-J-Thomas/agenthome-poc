using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Web.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class ExternalWebApplicationProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
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

    public static async Task<ExternalWebApplicationProcess> StartAsync(
        string workspaceRoot,
        int port,
        string codexExecutablePath,
        string model,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var webAssemblyPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.Web.dll");
        if (!File.Exists(webAssemblyPath))
        {
            throw new InvalidOperationException($"Expected Web assembly at {webAssemblyPath}.");
        }

        return await StartCoreAsync(
            webAssemblyPath,
            workspaceRoot,
            port,
            codexExecutablePath,
            model,
            [],
            environment);
    }

    public static async Task<ExternalWebApplicationProcess> StartBrowserProfileHostAsync(
        string workspaceRoot,
        int port,
        string codexExecutablePath,
        string model,
        string capabilityTrustRoot,
        IReadOnlyList<BrowserModelProfileSpec> profiles,
        IReadOnlyList<BrowserCommandActionSpec>? commandActions = null)
    {
        var hostAssemblyPath = typeof(BrowserProfileWebHost).Assembly.Location;
        if (!File.Exists(hostAssemblyPath))
        {
            throw new InvalidOperationException($"Expected browser-profile host assembly at {hostAssemblyPath}.");
        }
        var additionalArguments = new List<string>
        {
            "--browser-profile-web-host",
            "--capability-trust-root",
            capabilityTrustRoot,
        };
        foreach (var profile in profiles)
        {
            additionalArguments.Add("--additional-model-profile");
            additionalArguments.Add(BrowserProfileWebHost.Serialize(profile));
        }
        foreach (var commandAction in commandActions ?? [])
        {
            additionalArguments.Add("--command-action-registration");
            additionalArguments.Add(BrowserProfileWebHost.Serialize(commandAction));
        }

        var runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.E2ETests.runtimeconfig.json");
        var depsFilePath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.E2ETests.deps.json");
        return await StartCoreAsync(
            hostAssemblyPath,
            workspaceRoot,
            port,
            codexExecutablePath,
            model,
            additionalArguments,
            environment: null,
            dotnetExecRuntimeConfigPath: runtimeConfigPath,
            dotnetExecDepsFilePath: depsFilePath);
    }

    private static async Task<ExternalWebApplicationProcess> StartCoreAsync(
        string assemblyPath,
        string workspaceRoot,
        int port,
        string codexExecutablePath,
        string model,
        IReadOnlyList<string> additionalArguments,
        IReadOnlyDictionary<string, string>? environment,
        string? dotnetExecRuntimeConfigPath = null,
        string? dotnetExecDepsFilePath = null)
    {
        var output = new ProcessOutputBuffer();
        var error = new ProcessOutputBuffer();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "--workdir",
                workspaceRoot,
                "--port",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--model",
                model,
                "--codex-path",
                codexExecutablePath
            }
        };
        if (dotnetExecRuntimeConfigPath is not null && dotnetExecDepsFilePath is not null)
        {
            startInfo.ArgumentList.Insert(0, assemblyPath);
            startInfo.ArgumentList.Insert(0, dotnetExecDepsFilePath);
            startInfo.ArgumentList.Insert(0, "--depsfile");
            startInfo.ArgumentList.Insert(0, dotnetExecRuntimeConfigPath);
            startInfo.ArgumentList.Insert(0, "--runtimeconfig");
            startInfo.ArgumentList.Insert(0, "exec");
        }
        else
        {
            startInfo.ArgumentList.Insert(0, assemblyPath);
        }
        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var item in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("External Web process did not start.");
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

    public async Task StopAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            return;
        }

        using var signal = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/kill",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            ArgumentList = { "-INT", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }) ?? throw new InvalidOperationException("The external Web shutdown signal process did not start.");
        await signal.WaitForExitAsync();
        if (signal.ExitCode != 0)
        {
            throw new InvalidOperationException("The external Web shutdown signal process failed: " + await signal.StandardError.ReadToEndAsync());
        }

        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
