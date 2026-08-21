using System.Diagnostics;
using System.Text;
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

        var instanceId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        await TraceAsync(validated, instanceId, "process-start", new { arguments = commandArguments });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TraceSync(validated, instanceId, "process-exit", null);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => TraceSync(validated, instanceId, "process-error", new { error = args.ExceptionObject?.ToString() });

        const string ThreadId = "thread-test";
        string? pendingToolTurnId = null;
        string? pendingToolText = null;
        string? pendingToolPrompt = null;
        string? pendingToolCallId = null;
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

                var toolResponse = root.TryGetProperty("result", out var result)
                    ? result
                    : default;
                var toolText = toolResponse.ValueKind == JsonValueKind.Object && toolResponse.TryGetProperty("contentItems", out var contentItems) && contentItems.ValueKind == JsonValueKind.Array
                    ? string.Join("\n", contentItems.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null).Where(text => text is not null))
                    : string.Empty;
                var approved = toolResponse.ValueKind == JsonValueKind.Object
                    && toolResponse.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.True
                    && toolText.Contains("approved browser evidence", StringComparison.Ordinal);
                var succeeded = toolResponse.ValueKind == JsonValueKind.Object
                    && toolResponse.TryGetProperty("success", out var successElement)
                    && successElement.ValueKind == JsonValueKind.True;
                var brokerOutcome = ReadToolOutcome(toolText);
                var approvalRejected = !succeeded && string.Equals(brokerOutcome, "approvalrejected", StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(validated.GovernedToolPromptMarker))
                {
                    pendingToolText = approved
                        ? $"browser governed tool approved: {toolText}"
                        : approvalRejected
                            ? $"browser governed tool rejected: {toolText}"
                            : $"browser governed tool returned an unexpected outcome: {toolText}";
                }
                await TraceAsync(validated, instanceId, "tool-response", new
                {
                    threadId = ThreadId,
                    turnId = pendingToolTurnId,
                    callId = pendingToolCallId,
                    prompt = pendingToolPrompt,
                    success = succeeded,
                    approved,
                    brokerOutcome
                });
                await CompleteTurnAsync(ThreadId, pendingToolTurnId, pendingToolText!);
                await TraceAsync(validated, instanceId, "turn-completed", new { threadId = ThreadId, turnId = pendingToolTurnId, prompt = pendingToolPrompt, approved, outcome = approved ? "approved" : approvalRejected ? "rejected" : "unexpected" });
                pendingToolTurnId = null;
                pendingToolText = null;
                pendingToolPrompt = null;
                pendingToolCallId = null;
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
                    await TraceAsync(validated, instanceId, "initialize", new { requestId = id });
                    await WriteAsync(new { id, result = new { } });
                    break;

                case "initialized":
                    break;

                case "model/list":
                    await TraceAsync(validated, instanceId, "model-list", new { requestId = id });
                    await WriteAsync(new { id, result = new { data = validated.AdvertisedModels.Select(model => new { id = model, model }).ToArray(), nextCursor = (string?)null } });
                    break;

                case "thread/start":
                    var model = root.GetProperty("params").GetProperty("model").GetString() ?? string.Empty;
                    var modelProvider = root.GetProperty("params").GetProperty("modelProvider").GetString() ?? string.Empty;
                    await TraceAsync(validated, instanceId, "thread-start", new { requestId = id, threadId = ThreadId });
                    await WriteAsync(new { id, result = new { model, modelProvider, thread = new { id = ThreadId, modelProvider } } });
                    break;

                case "turn/start":
                    var turnId = "turn-test";
                    var inputText = ReadInputText(root);
                    var prompt = ReadCurrentUserMessage(inputText);
                    var correlationPrompt = ReadAdmittedTriggerPrompt(inputText) ?? prompt;
                    var text = validated.ResponsePrefix + prompt;
                    await TraceAsync(validated, instanceId, "turn-start", new { requestId = id, threadId = ThreadId, turnId, prompt = correlationPrompt });
                    await WriteAsync(new { id, result = new { turn = new { id = turnId } } });
                    if (validated.RequestGovernedTool
                        && (string.IsNullOrWhiteSpace(validated.GovernedToolPromptMarker)
                            || inputText.Contains(validated.GovernedToolPromptMarker, StringComparison.Ordinal)))
                    {
                        pendingToolTurnId = turnId;
                        pendingToolText = "continued after governed tool denial";
                        pendingToolPrompt = correlationPrompt;
                        pendingToolCallId = "call-browser-" + turnId;
                        await WriteAsync(new
                        {
                            id = 99,
                            method = "item/tool/call",
                            @params = new
                            {
                                threadId = ThreadId,
                                turnId,
                                callId = pendingToolCallId,
                                @namespace = "embodysense",
                                tool = "command",
                                arguments = new { command = "read", path = validated.GovernedToolPath ?? "approval-only-note.txt" }
                            }
                        });
                        await TraceAsync(validated, instanceId, "tool-call", new { threadId = ThreadId, turnId, callId = pendingToolCallId, prompt = correlationPrompt, @namespace = "embodysense", tool = "command", path = validated.GovernedToolPath ?? "approval-only-note.txt" });
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

                    if (validated.TurnFailureMessage is not null
                        && (string.IsNullOrWhiteSpace(validated.TurnFailurePromptMarker)
                            || inputText.Contains(validated.TurnFailurePromptMarker, StringComparison.Ordinal)))
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
                        await TraceAsync(validated, instanceId, "turn-failed", new { threadId = ThreadId, turnId, prompt = correlationPrompt, detail = validated.TurnFailureMessage });
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

    private static string ReadInputText(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join("\n", input.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null).Where(text => text is not null));
    }

    private static string ReadCurrentUserMessage(string inputText)
    {
        const string Marker = "Current user message:";
        var markerIndex = inputText.IndexOf(Marker, StringComparison.Ordinal);
        return markerIndex < 0 ? inputText : inputText[(markerIndex + Marker.Length)..].Trim();
    }

    private static string? ReadAdmittedTriggerPrompt(string inputText)
    {
        const string TriggerMarker = "[EmbodySense untrusted trigger prompt data]";
        const string RestoredUserEndMarker = "[/restored user message]";
        var markerIndex = inputText.IndexOf(TriggerMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var promptStart = markerIndex + TriggerMarker.Length;
        var promptEnd = inputText.IndexOf(RestoredUserEndMarker, promptStart, StringComparison.Ordinal);
        if (promptEnd < 0)
        {
            return null;
        }

        var prompt = inputText[promptStart..promptEnd].Trim();
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
    }

    private static string? ReadToolOutcome(string toolText)
    {
        const string Prefix = "outcome:";
        foreach (var line in toolText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var outcome = trimmed[Prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(outcome) ? null : outcome;
            }
        }

        return null;
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

    private static Task TraceAsync(CodexConversationProbeConfiguration configuration, string instanceId, string stage, object? details)
    {
        if (string.IsNullOrWhiteSpace(configuration.ProtocolTracePath))
        {
            return Task.CompletedTask;
        }

        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["stage"] = stage,
            ["instanceId"] = instanceId,
            ["processId"] = Environment.ProcessId,
            ["timestampUtc"] = DateTimeOffset.UtcNow
        };
        if (details is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(details).EnumerateObject())
            {
                payload[property.Name] = property.Value.Clone();
            }
        }

        return AppendTraceAsync(configuration.ProtocolTracePath, JsonSerializer.Serialize(payload) + Environment.NewLine);
    }

    private static async Task AppendTraceAsync(string path, string line)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line));
    }

    private static void TraceSync(CodexConversationProbeConfiguration configuration, string instanceId, string stage, object? details)
    {
        try
        {
            TraceAsync(configuration, instanceId, stage, details).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
