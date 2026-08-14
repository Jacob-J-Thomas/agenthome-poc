using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.CancellationHost.CodexAppServer;

internal static class CodexConversationProbeHost
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<int> RunAsync(string configurationPath, string[] commandArguments)
    {
        CodexConversationProbeConfiguration? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<CodexConversationProbeConfiguration>(await File.ReadAllTextAsync(configurationPath), _jsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return 2;
        }

        if (!IsValid(configuration))
        {
            return 2;
        }
        var validated = configuration!;

        if (commandArguments.Contains("--version", StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync(validated.Version);
            return 0;
        }

        const string ThreadId = "thread-test";
        string? pendingToolTurnId = null;
        string? pendingToolText = null;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (IsToolResponse(root) && pendingToolTurnId is not null)
            {
                if (validated.ToolResponsePath is not null)
                {
                    await File.WriteAllTextAsync(validated.ToolResponsePath, line);
                }

                await CompleteTurnAsync(ThreadId, pendingToolTurnId, pendingToolText!);
                pendingToolTurnId = null;
                pendingToolText = null;
                continue;
            }

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var method = methodElement.GetString();
            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
            switch (method)
            {
                case "initialize":
                    await WriteAsync(new { id, result = new { } });
                    break;

                case "initialized":
                    break;

                case "model/list":
                    await WriteAsync(new { id, result = new { data = validated.AdvertisedModels.Select(model => new { id = model, model }).ToArray(), nextCursor = (string?)null } });
                    break;

                case "thread/start":
                    await WriteAsync(new { id, result = new { thread = new { id = ThreadId } } });
                    break;

                case "turn/start":
                    var turnId = "turn-test";
                    var text = validated.ResponsePrefix + ReadCurrentUserMessage(root);
                    await WriteAsync(new { id, result = new { turn = new { id = turnId } } });
                    if (validated.RequestGovernedTool)
                    {
                        pendingToolTurnId = turnId;
                        pendingToolText = "continued after governed tool denial";
                        await WriteAsync(new
                        {
                            id = 99,
                            method = "item/tool/call",
                            @params = new
                            {
                                threadId = ThreadId,
                                turnId,
                                callId = "call-owner-disconnect",
                                @namespace = "embodysense",
                                tool = "command",
                                arguments = new { command = "read", path = "approval-only-note.txt" }
                            }
                        });
                        break;
                    }

                    if (validated.WaitForTurnRelease)
                    {
                        await File.WriteAllTextAsync(validated.TurnReadyMarkerPath!, "started");
                        if (!await WaitForReleaseAsync(validated.TurnReleaseMarkerPath!))
                        {
                            return 9;
                        }
                    }

                    if (validated.TurnFailureMessage is not null)
                    {
                        await WriteAsync(new
                        {
                            method = "turn/completed",
                            @params = new
                            {
                                threadId = ThreadId,
                                turnId,
                                turn = new { id = turnId, status = "failed", error = new { message = validated.TurnFailureMessage }, items = Array.Empty<object>() }
                            }
                        });
                        break;
                    }

                    await CompleteTurnAsync(ThreadId, turnId, text);
                    break;
            }
        }

        return 0;
    }

    private static bool IsValid(CodexConversationProbeConfiguration? configuration)
        => configuration is not null
            && !string.IsNullOrWhiteSpace(configuration.Version)
            && configuration.AdvertisedModels is { Length: > 0 and <= 128 }
            && configuration.AdvertisedModels.All(model => !string.IsNullOrWhiteSpace(model))
            && !string.IsNullOrWhiteSpace(configuration.ResponsePrefix)
            && configuration.ResponsePrefix.Length <= 128
            && !(configuration.WaitForTurnRelease && configuration.RequestGovernedTool)
            && (!configuration.WaitForTurnRelease
                || (!string.IsNullOrWhiteSpace(configuration.TurnReadyMarkerPath) && !string.IsNullOrWhiteSpace(configuration.TurnReleaseMarkerPath)))
            && (!configuration.RequestGovernedTool || !string.IsNullOrWhiteSpace(configuration.ToolResponsePath));

    private static bool IsToolResponse(JsonElement root)
        => root.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value == 99 && !root.TryGetProperty("method", out _);

    private static string ReadCurrentUserMessage(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var inputText = string.Join("\n", input.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null).Where(text => text is not null));
        const string Marker = "Current user message:";
        var markerIndex = inputText.IndexOf(Marker, StringComparison.Ordinal);
        return markerIndex < 0 ? inputText : inputText[(markerIndex + Marker.Length)..].Trim();
    }

    private static async Task<bool> WaitForReleaseAsync(string releasePath)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(releasePath))
        {
            if (stopwatch.Elapsed >= TimeSpan.FromMinutes(2))
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    private static async Task CompleteTurnAsync(string threadId, string turnId, string text)
    {
        await WriteAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, delta = text } });
        await WriteAsync(new
        {
            method = "turn/completed",
            @params = new
            {
                threadId,
                turnId,
                turn = new { id = turnId, status = "completed", items = new[] { new { type = "agentMessage", phase = "final_answer", text } } }
            }
        });
    }

    private static async Task WriteAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, _jsonOptions));
        await Console.Out.FlushAsync();
    }
}
