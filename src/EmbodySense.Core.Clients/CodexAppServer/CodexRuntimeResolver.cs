using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Clients.CodexAppServer.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Discovers and probes a compatible Codex executable without starting a durable inference session.
/// </summary>
/// <remarks>
/// An explicit path is authoritative. Otherwise, Windows desktop installations are considered before <c>PATH</c> entries.
/// Each candidate is bounded by a shared fifteen-second probe deadline, and diagnostics are truncated before being returned.
/// Resolution reports incompatibility as data; caller cancellation still propagates.
/// </remarks>
public sealed class CodexRuntimeResolver
{
    private const int MaxDiagnosticCharacters = 2_000;
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Resolves the first executable that starts app-server and, when configured, advertises the requested model.
    /// </summary>
    /// <param name="explicitExecutablePath">An authoritative executable path or command name, or <see langword="null"/> for discovery.</param>
    /// <param name="configuredModel">The model that must be advertised, or <see langword="null"/> to accept external model selection.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the Codex runtime resolution.</returns>
    public async Task<CodexRuntimeResolution> ResolveAsync(string? explicitExecutablePath, string? configuredModel, CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates(explicitExecutablePath).ToArray();
        if (!string.IsNullOrWhiteSpace(explicitExecutablePath) && (candidates.Length == 0 || !File.Exists(candidates[0].ExecutablePath)))
        {
            return new CodexRuntimeResolution(
                CodexRuntimeResolutionStatus.ExecutableNotFound,
                candidates.FirstOrDefault()?.ExecutablePath ?? explicitExecutablePath,
                null,
                configuredModel,
                "explicit --codex-path",
                $"The explicit Codex executable `{explicitExecutablePath}` does not exist. Update `--codex-path` to a usable Codex executable.");
        }

        if (candidates.Length == 0)
        {
            return new CodexRuntimeResolution(
                CodexRuntimeResolutionStatus.ExecutableNotFound,
                null,
                null,
                configuredModel,
                null,
                "No Codex executable was found. Install or update Codex, or pass `--codex-path <path>`.");
        }

        var failures = new List<string>();
        CodexRuntimeCandidate? firstFailedCandidate = null;
        CodexRuntimeProbeResult? firstFailedProbe = null;
        CodexRuntimeCandidate? modelUnavailableCandidate = null;
        CodexRuntimeProbeResult? modelUnavailableProbe = null;
        foreach (var candidate in candidates)
        {
            var probe = await ProbeAsync(candidate.ExecutablePath, configuredModel, cancellationToken);
            if (probe.IsUsable)
            {
                return new CodexRuntimeResolution(
                    CodexRuntimeResolutionStatus.Compatible,
                    candidate.ExecutablePath,
                    probe.Version,
                    configuredModel,
                    candidate.Source,
                    probe.Detail);
            }

            firstFailedCandidate ??= candidate;
            firstFailedProbe ??= probe;
            if (probe.Detail.StartsWith("Configured model ", StringComparison.Ordinal))
            {
                modelUnavailableCandidate ??= candidate;
                modelUnavailableProbe ??= probe;
            }

            failures.Add($"{candidate.ExecutablePath}: {probe.Detail}");
        }

        var status = modelUnavailableCandidate is null ? CodexRuntimeResolutionStatus.ProbeFailed : CodexRuntimeResolutionStatus.ModelUnavailable;
        var detail = status == CodexRuntimeResolutionStatus.ModelUnavailable
            ? $"No discovered Codex executable advertises model `{configuredModel}`. Update Codex or pass a compatible executable with `--codex-path`. Attempts: {string.Join(" | ", failures)}"
            : $"No discovered Codex executable passed the runtime probe. Update Codex or pass a compatible executable with `--codex-path`. Attempts: {string.Join(" | ", failures)}";
        var relevantCandidate = modelUnavailableCandidate ?? firstFailedCandidate ?? candidates[0];
        var relevantProbe = modelUnavailableProbe ?? firstFailedProbe;
        return new CodexRuntimeResolution(status, relevantCandidate.ExecutablePath, relevantProbe?.Version, configuredModel, relevantCandidate.Source, LimitDiagnostic(detail));
    }

    private static IReadOnlyList<CodexRuntimeCandidate> GetCandidates(string? explicitExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitExecutablePath))
        {
            return [new CodexRuntimeCandidate(ResolveExplicitPath(explicitExecutablePath), "explicit --codex-path")];
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = new List<CodexRuntimeCandidate>();
        var seen = new HashSet<string>(comparer);
        AddDesktopCandidates(candidates, seen);
        AddPathCandidates(candidates, seen);
        return candidates;
    }

    private static void AddDesktopCandidates(List<CodexRuntimeCandidate> candidates, HashSet<string> seen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var localApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return;
        }

        var root = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateDirectories()
                .SelectMany(directory => new[] { new FileInfo(Path.Combine(directory.FullName, "codex.exe")), new FileInfo(Path.Combine(directory.FullName, "codex.cmd")) })
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            AddCandidate(candidates, seen, file.FullName, "Codex Desktop");
        }
    }

    private static void AddPathCandidates(List<CodexRuntimeCandidate> candidates, HashSet<string> seen)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var executableNames = OperatingSystem.IsWindows() ? new[] { "codex.exe", "codex.cmd", "codex" } : ["codex"];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    AddCandidate(candidates, seen, Path.GetFullPath(candidate), "PATH");
                }
            }
        }
    }

    private static void AddCandidate(List<CodexRuntimeCandidate> candidates, HashSet<string> seen, string executablePath, string source)
    {
        if (seen.Add(executablePath))
        {
            candidates.Add(new CodexRuntimeCandidate(executablePath, source));
        }
    }

    private static string ResolveExplicitPath(string explicitExecutablePath)
    {
        if (File.Exists(explicitExecutablePath))
        {
            return Path.GetFullPath(explicitExecutablePath);
        }

        if (Path.IsPathFullyQualified(explicitExecutablePath) || explicitExecutablePath.Contains(Path.DirectorySeparatorChar) || explicitExecutablePath.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.GetFullPath(explicitExecutablePath);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, explicitExecutablePath);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return explicitExecutablePath;
    }

    private static async Task<CodexRuntimeProbeResult> ProbeAsync(string executablePath, string? configuredModel, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_probeTimeout);
        var probeCancellationToken = deadline.Token;
        string? version;
        try
        {
            version = await ReadVersionAsync(executablePath, probeCancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CodexRuntimeProbeResult(false, null, $"Version probe timed out after {_probeTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CodexRuntimeProbeResult(false, null, LimitDiagnostic($"Version probe failed: {exception.Message}"));
        }

        try
        {
            var advertisedModels = await ReadAdvertisedModelsAsync(executablePath, configuredModel, probeCancellationToken);
            if (string.IsNullOrWhiteSpace(configuredModel))
            {
                return new CodexRuntimeProbeResult(true, version, "Codex app-server started successfully; model selection is externally configured.");
            }

            if (!advertisedModels.Contains(configuredModel, StringComparer.Ordinal))
            {
                return new CodexRuntimeProbeResult(false, version, $"Configured model `{configuredModel}` is not advertised by Codex {version ?? "(unknown version)"}.");
            }

            return new CodexRuntimeProbeResult(true, version, $"Codex {version ?? "(unknown version)"} advertises configured model `{configuredModel}`.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CodexRuntimeProbeResult(false, version, $"App-server compatibility probe timed out after {_probeTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CodexRuntimeProbeResult(false, version, LimitDiagnostic($"App-server compatibility probe failed: {exception.Message}"));
        }
    }

    private static async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Codex version probe did not start.");
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await standardOutput).Trim();
            var error = (await standardError).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Codex version probe exited with code {process.ExitCode}." : error);
            }

            return string.IsNullOrWhiteSpace(output) ? null : LimitDiagnostic(output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadAdvertisedModelsAsync(string executablePath, string? configuredModel, CancellationToken cancellationToken)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "embodysense-codex-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var options = new LlmInferenceClientOptions
            {
                Surface = LlmInferenceSurface.OpenAiCodex,
                CodexExecutablePath = executablePath
            };
            await using var transport = new CodexAppServerProcessTransport(options, workingDirectory);
            await transport.WriteLineAsync(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "embodysense-runtime-probe",
                        ["title"] = "EmbodySense runtime probe",
                        ["version"] = "0.1.0"
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["experimentalApi"] = true
                    }
                }
            }.ToJsonString(), cancellationToken);
            _ = await ReadResponseAsync(transport, 1, cancellationToken);
            await transport.WriteLineAsync(new JsonObject
            {
                ["method"] = "initialized",
                ["params"] = new JsonObject()
            }.ToJsonString(), cancellationToken);
            var models = new HashSet<string>(StringComparer.Ordinal);
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var requestId = 2;
            do
            {
                var parameters = new JsonObject
                {
                    ["includeHidden"] = true,
                    ["limit"] = 1_000
                };
                if (cursor is not null)
                {
                    parameters["cursor"] = cursor;
                }

                await transport.WriteLineAsync(new JsonObject
                {
                    ["id"] = requestId,
                    ["method"] = "model/list",
                    ["params"] = parameters
                }.ToJsonString(), cancellationToken);
                var response = await ReadResponseAsync(transport, requestId, cancellationToken);
                cursor = AddAdvertisedModels(response, models);
                if (string.IsNullOrWhiteSpace(configuredModel) || models.Contains(configuredModel))
                {
                    return models.ToArray();
                }

                if (cursor is not null && !seenCursors.Add(cursor))
                {
                    throw new InvalidOperationException("Codex app-server model/list response repeated a continuation cursor.");
                }

                requestId++;
            }
            while (cursor is not null);

            return models.ToArray();
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(ICodexAppServerTransport transport, int requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await transport.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var detail = string.IsNullOrWhiteSpace(transport.ErrorOutput) ? "Codex app-server closed its output stream." : transport.ErrorOutput.Trim();
                throw new InvalidOperationException(detail);
            }

            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == requestId)
            {
                if (message.TryGetProperty("error", out var error))
                {
                    var errorMessage = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : error.GetRawText();
                    throw new InvalidOperationException(errorMessage ?? "Codex app-server probe failed.");
                }

                return message.Clone();
            }

            if (message.TryGetProperty("id", out id) && message.TryGetProperty("method", out _))
            {
                await transport.WriteLineAsync(new JsonObject
                {
                    ["id"] = JsonNode.Parse(id.GetRawText()),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = "Runtime compatibility probing does not handle server requests."
                    }
                }.ToJsonString(), cancellationToken);
            }
        }
    }

    private static string? AddAdvertisedModels(JsonElement response, HashSet<string> models)
    {
        if (!response.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Codex app-server model/list response did not contain a model catalog.");
        }

        foreach (var item in data.EnumerateArray())
        {
            AddString(item, "model", models);
            AddString(item, "id", models);
        }

        if (!result.TryGetProperty("nextCursor", out var nextCursor) || nextCursor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (nextCursor.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nextCursor.GetString()))
        {
            throw new InvalidOperationException("Codex app-server model/list response contained an invalid continuation cursor.");
        }

        return nextCursor.GetString();
    }

    private static void AddString(JsonElement item, string propertyName, HashSet<string> values)
    {
        if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            values.Add(value.GetString()!);
        }
    }

    private static string LimitDiagnostic(string value)
    {
        return value.Length <= MaxDiagnosticCharacters ? value : value[^MaxDiagnosticCharacters..];
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
