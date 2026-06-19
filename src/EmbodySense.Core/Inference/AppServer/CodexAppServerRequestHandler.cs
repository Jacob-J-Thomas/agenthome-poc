using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Inference.Interfaces;

namespace EmbodySense.Core.Inference.AppServer;

internal sealed class CodexAppServerRequestHandler : ICodexAppServerRequestHandler
{
    private readonly ICodexAppServerToolBridge? _toolBridge;
    private readonly IAuditLog? _auditLog;

    public CodexAppServerRequestHandler(ICodexAppServerToolBridge? toolBridge, IAuditLog? auditLog)
    {
        _toolBridge = toolBridge;
        _auditLog = auditLog;
    }

    public async Task<CodexAppServerRequestHandlingResult> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        JsonObject? result = method switch
        {
            "item/tool/call" when _toolBridge is not null => await _toolBridge.HandleToolCallAsync(parameters, cancellationToken),
            "item/commandExecution/requestApproval" => new JsonObject { ["decision"] = "decline" },
            "item/fileChange/requestApproval" => new JsonObject { ["decision"] = "decline" },
            "applyPatchApproval" => new JsonObject { ["decision"] = "decline" },
            "execCommandApproval" => new JsonObject { ["decision"] = "decline" },
            "item/permissions/requestApproval" => new JsonObject
            {
                ["permissions"] = new JsonObject
                {
                    ["fileSystem"] = null,
                    ["network"] = null
                },
                ["scope"] = "turn",
                ["strictAutoReview"] = true
            },
            "mcpServer/elicitation/request" => new JsonObject
            {
                ["action"] = "decline",
                ["content"] = null
            },
            "item/tool/requestUserInput" => new JsonObject
            {
                ["answers"] = new JsonObject()
            },
            _ => null
        };

        if (result is null)
        {
            await RecordAppServerRequestAsync(method, parameters, AuditSchema.Outcomes.Failed, "Rejected unsupported Codex app-server request.", cancellationToken);
            return new CodexAppServerRequestHandlingResult(false, null);
        }

        var outcome = GetHandledRequestOutcome(method, result);
        await RecordAppServerRequestAsync(method, parameters, outcome, GetHandledRequestDetail(method, outcome), cancellationToken);
        return new CodexAppServerRequestHandlingResult(true, result);
    }

    private Task RecordAppServerRequestAsync(
        string method,
        JsonElement parameters,
        string outcome,
        string detail,
        CancellationToken cancellationToken)
    {
        if (_auditLog is null)
        {
            return Task.CompletedTask;
        }

        return _auditLog.AppendAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Llm,
            action: AuditSchema.Actions.LlmAppServerRequest,
            target: method,
            outcome: outcome,
            detail: detail,
            metadata: CreateAppServerRequestMetadata(method, parameters)), cancellationToken);
    }

    private static Dictionary<string, object?> CreateAppServerRequestMetadata(string method, JsonElement parameters)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["thread_id"] = TryGetString(parameters, "threadId"),
            ["turn_id"] = TryGetString(parameters, "turnId"),
            ["item_id"] = TryGetString(parameters, "itemId"),
            ["call_id"] = TryGetString(parameters, "callId"),
            ["namespace"] = TryGetString(parameters, "namespace"),
            ["tool"] = TryGetString(parameters, "tool")
        };

        return metadata
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value);
    }

    private static string GetHandledRequestOutcome(string method, JsonObject result)
    {
        if (method != "item/tool/call")
        {
            return AuditSchema.Outcomes.Denied;
        }

        return IsSuccessfulToolResult(result) ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.Failed;
    }

    private static string GetHandledRequestDetail(string method, string outcome)
    {
        if (method != "item/tool/call")
        {
            return "Declined native Codex app-server request.";
        }

        return outcome == AuditSchema.Outcomes.Succeeded
            ? "Handled Codex app-server dynamic tool request."
            : "Rejected Codex app-server dynamic tool request.";
    }

    private static bool IsSuccessfulToolResult(JsonObject result)
    {
        return result.TryGetPropertyValue("success", out var success) && success is not null && success.GetValue<bool>();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
