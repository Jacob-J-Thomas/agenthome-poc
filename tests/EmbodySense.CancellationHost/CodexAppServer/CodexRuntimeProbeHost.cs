using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.CancellationHost.CodexAppServer;

internal static class CodexRuntimeProbeHost
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<int> RunAsync(string configurationPath, string[] commandArguments)
    {
        CodexRuntimeProbeConfiguration? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<CodexRuntimeProbeConfiguration>(await File.ReadAllTextAsync(configurationPath), _jsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return 2;
        }
        if (configuration is null
            || string.IsNullOrWhiteSpace(configuration.Version)
            || configuration.AdvertisedModels is null
            || configuration.AdvertisedModels.Any(string.IsNullOrWhiteSpace)
            || configuration.VersionDelayMilliseconds is < 0 or > 60_000
            || configuration.ProtocolStageDelayMilliseconds is < 0 or > 60_000
            || configuration.ModelPageSize < 1)
        {
            return 2;
        }

        if (commandArguments.Contains("--version", StringComparer.Ordinal))
        {
            await DelayAsync(configuration.VersionDelayMilliseconds);
            if (configuration.VersionExitCode != 0)
            {
                await Console.Error.WriteLineAsync("simulated version failure");
                return configuration.VersionExitCode;
            }

            await Console.Out.WriteLineAsync(configuration.Version);
            return 0;
        }

        if (configuration.FailAppServer)
        {
            await Console.Error.WriteLineAsync("simulated app-server startup failure");
            return 7;
        }

        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var method = methodElement.GetString();
            var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var parsedId) ? parsedId : 0;
            switch (method)
            {
                case "initialize":
                    await MarkStageAsync(configuration.ProtocolStageMarkerPath, "initialize-started");
                    await DelayAsync(configuration.ProtocolStageDelayMilliseconds);
                    await MarkStageAsync(configuration.ProtocolStageMarkerPath, "initialize-completed");
                    if (configuration.RequestBeforeInitialize)
                    {
                        await WriteAsync(new { id = 99, method = "unsupported/probe", @params = new { } });
                        var responseLine = await Console.In.ReadLineAsync();
                        if (!IsMethodNotFoundResponse(responseLine))
                        {
                            await Console.Error.WriteLineAsync("runtime probe did not decline the server request");
                            return 8;
                        }
                    }
                    await WriteAsync(new { id, result = new { } });
                    break;

                case "initialized":
                    break;

                case "model/list":
                    await MarkStageAsync(configuration.ProtocolStageMarkerPath, "model-list-started");
                    await DelayAsync(configuration.ProtocolStageDelayMilliseconds);
                    await MarkStageAsync(configuration.ProtocolStageMarkerPath, "model-list-completed");
                    var offset = ReadCursorOffset(root);
                    var page = configuration.AdvertisedModels.Skip(offset).Take(configuration.ModelPageSize).ToArray();
                    if (configuration.OmitModelCatalog)
                    {
                        await WriteAsync(new { id, result = new { } });
                        break;
                    }
                    var nextOffset = checked(offset + page.Length);
                    var nextCursor = nextOffset < configuration.AdvertisedModels.Length ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                    await WriteAsync(new { id, result = new { data = page.Select(model => new { id = model, model }).ToArray(), nextCursor } });
                    break;

                case "thread/start":
                    if (configuration.LegacyThreadStartShape)
                    {
                        await WriteAsync(new { id, result = new { thread = new { id = "thread-probe" } } });
                        break;
                    }

                    var requestedModel = root.GetProperty("params").TryGetProperty("model", out var modelElement)
                        ? modelElement.GetString()
                        : configuration.AdvertisedModels.FirstOrDefault() ?? "externally-configured";
                    var requestedProvider = root.GetProperty("params").GetProperty("modelProvider").GetString() ?? string.Empty;
                    await WriteAsync(new { id, result = new { model = requestedModel, modelProvider = requestedProvider, thread = new { id = "thread-probe", modelProvider = requestedProvider } } });
                    break;
            }
        }

        return 0;
    }

    private static Task DelayAsync(int delayMilliseconds)
        => delayMilliseconds == 0 ? Task.CompletedTask : Task.Delay(delayMilliseconds);

    private static Task MarkStageAsync(string? markerPath, string stage)
        => string.IsNullOrWhiteSpace(markerPath) ? Task.CompletedTask : File.AppendAllTextAsync(markerPath, stage + Environment.NewLine);

    private static int ReadCursorOffset(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("cursor", out var cursor)
            || cursor.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        return cursor.ValueKind == JsonValueKind.String
            && int.TryParse(cursor.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset)
            && offset >= 0
                ? offset
                : 0;
    }

    private static bool IsMethodNotFoundResponse(string? line)
    {
        if (line is null)
        {
            return false;
        }

        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        return root.TryGetProperty("id", out var id)
            && id.TryGetInt32(out var idValue)
            && idValue == 99
            && root.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
            && code.TryGetInt32(out var codeValue)
            && codeValue == -32601;
    }

    private static async Task WriteAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, _jsonOptions));
        await Console.Out.FlushAsync();
    }

    private sealed record CodexRuntimeProbeConfiguration(
        string Version,
        string[] AdvertisedModels,
        bool FailAppServer,
        int VersionExitCode,
        bool OmitModelCatalog,
        bool RequestBeforeInitialize,
        int VersionDelayMilliseconds,
        int ProtocolStageDelayMilliseconds,
        string? ProtocolStageMarkerPath,
        int ModelPageSize,
        bool LegacyThreadStartShape);
}
