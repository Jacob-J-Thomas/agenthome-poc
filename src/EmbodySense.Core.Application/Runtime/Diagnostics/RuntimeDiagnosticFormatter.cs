using System.Text;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.Diagnostics;

public static class RuntimeDiagnosticFormatter
{
    public static string FormatVerboseContext(IReadOnlyList<LlmMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // TODO(verbose-context-provenance): Replace this message dump with a structured visible-context model that includes source provenance,
        // memory/context omissions, active loop/run identity, authority, and compaction state before verbose mode is treated as complete.
        var builder = new StringBuilder();
        builder.AppendLine("[verbose] Visible inference context follows.");
        builder.AppendLine("[verbose] This is the startup, restored, and session context EmbodySense is sending for the next model turn.");
        builder.AppendLine("[verbose] This is not private model reasoning, hidden chain-of-thought, or provider-internal state.");
        foreach (var message in messages)
        {
            builder.AppendLine(FormatMessageHeader(message.Role));
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatMessageHeader(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "System:",
            LlmMessageRole.User => "User:",
            LlmMessageRole.Assistant => "Assistant:",
            LlmMessageRole.Tool => "Tool:",
            _ => role.ToString()
        };
    }
}
