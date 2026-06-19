using System.Text;
using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal sealed class CodexAppServerContextBuilder : ICodexAppServerContextBuilder
{
    private const int MaxDeveloperInstructionContextCharacters = 24_000;

    public string CreateDeveloperInstructions(LlmInferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();
        builder.AppendLine("""
            You are running inside EmbodySense through the Codex app-server protocol.

            EmbodySense governs the user workspace. Do not use Codex-native shell, filesystem, MCP, browser, web-search, subagent, or permission-escalation tools for workspace actions. The app-server working directory is an inert runtime directory, not the user workspace.

            For any workspace action, use only the `embodysense.command` dynamic tool. It enforces `.agent/permissions.json`, routes approval when required, and writes EmbodySense audit events. Do not claim a workspace action succeeded until the corresponding EmbodySense tool result says it succeeded.
            """);

        var restoredContext = FormatRestoredContext(request);
        if (!string.IsNullOrWhiteSpace(restoredContext))
        {
            builder.AppendLine();
            builder.AppendLine(restoredContext);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatRestoredContext(LlmInferenceRequest request)
    {
        var latestUserIndex = FindLatestUserMessageIndex(request.Messages);
        var contextMessages = request.Messages
            .Take(latestUserIndex)
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.User or LlmMessageRole.Assistant)
            .ToArray();

        if (contextMessages.Length == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Restored EmbodySense context for this fresh provider thread:");
        foreach (var message in contextMessages)
        {
            builder.AppendLine();
            builder.AppendLine($"[{message.Role.ToString().ToLowerInvariant()}]");
            builder.AppendLine(message.Content.Trim());
        }

        var context = builder.ToString().TrimEnd();
        return context.Length <= MaxDeveloperInstructionContextCharacters
            ? context
            : context[^MaxDeveloperInstructionContextCharacters..];
    }

    private static int FindLatestUserMessageIndex(IReadOnlyList<LlmMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == LlmMessageRole.User)
            {
                return i;
            }
        }

        return messages.Count;
    }
}
