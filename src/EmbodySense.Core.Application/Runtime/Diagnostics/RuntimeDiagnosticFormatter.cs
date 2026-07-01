using System.Text;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Runtime.Diagnostics;

public static class RuntimeDiagnosticFormatter
{
    public static string FormatVerboseContext(RuntimeVerboseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder();
        builder.AppendLine("[verbose] Visible inference context follows.");
        builder.AppendLine("[verbose] This is the logical runtime context EmbodySense assembled before provider adapter formatting.");
        builder.AppendLine("[verbose] This is not private model reasoning, hidden chain-of-thought, or provider-internal state.");
        builder.AppendLine();
        AppendLoopBlock(builder, context);
        builder.AppendLine();
        AppendAuthorityBlock(builder, context.LoopDefinition);
        builder.AppendLine();
        AppendOmissionBlock(builder, context);
        builder.AppendLine();
        AppendMessages(builder, context.Messages);

        return builder.ToString().TrimEnd();
    }

    private static void AppendLoopBlock(StringBuilder builder, RuntimeVerboseContext context)
    {
        builder.AppendLine("[verbose] Active loop:");
        builder.AppendLine($"[verbose] - loop_id: {context.LoopDefinition.Id}");
        builder.AppendLine($"[verbose] - run_id: {context.RunIdentity.RunId}");
        builder.AppendLine($"[verbose] - role_id: {context.RunIdentity.RoleId ?? context.LoopDefinition.RoleId}");
        builder.AppendLine($"[verbose] - surface: {context.Surface.Id}");
        builder.AppendLine($"[verbose] - trigger: {context.LoopDefinition.Trigger}");
        builder.AppendLine($"[verbose] - memory_scope: {context.LoopDefinition.MemoryScope}");
        builder.AppendLine($"[verbose] - edit_mode: {context.LoopDefinition.EditMode}");
        builder.AppendLine($"[verbose] - graph_entry_node: {context.LoopDefinition.Graph?.EntryNodeId ?? "(none)"}");
        builder.AppendLine($"[verbose] - graph_terminal_nodes: {FormatList(context.LoopDefinition.Graph?.TerminalNodeIds ?? [])}");
        builder.AppendLine($"[verbose] - graph_nodes: {FormatGraphNodes(context.LoopDefinition.Graph?.Nodes ?? [])}");
        builder.AppendLine($"[verbose] - review_policy: {context.LoopDefinition.ReviewPolicy}");
        builder.AppendLine($"[verbose] - failure_policy: {context.LoopDefinition.FailurePolicy}");
    }

    private static void AppendAuthorityBlock(StringBuilder builder, LoopDefinition loopDefinition)
    {
        var availableCommands = Enum.GetValues<ToolCommand>()
            .Where(command => LoopCapabilityIds.AllowsWorkspaceCommand(loopDefinition.CapabilityIds, command))
            .Select(command => command.ToString().ToLowerInvariant())
            .ToArray();

        builder.AppendLine("[verbose] Active authority:");
        builder.AppendLine($"[verbose] - capability_ids: {string.Join(", ", loopDefinition.CapabilityIds)}");
        builder.AppendLine($"[verbose] - workspace_commands_allowed_by_loop: {FormatList(availableCommands)}");
        builder.AppendLine("[verbose] - workspace_permission_policy: .agent/permissions.json is evaluated after loop capability filtering for each workspace command request.");
    }

    private static void AppendOmissionBlock(StringBuilder builder, RuntimeVerboseContext context)
    {
        builder.AppendLine("[verbose] Context status, limits, and omissions:");
        builder.AppendLine($"[verbose] - compaction: {context.CompactionStatus}");
        foreach (var omission in context.Omissions)
        {
            builder.AppendLine($"[verbose] - {omission.Source} ({omission.Stage}): {omission.Reason}");
        }
    }

    private static void AppendMessages(StringBuilder builder, IReadOnlyList<RuntimeContextMessage> messages)
    {
        builder.AppendLine("[verbose] Runtime messages:");
        for (var i = 0; i < messages.Count; i++)
        {
            var item = messages[i];
            builder.AppendLine($"[verbose] message {i + 1}: role={FormatRole(item.Message.Role)} source={FormatSource(item.Source)}");
            builder.AppendLine($"[verbose] source_detail: {item.Detail}");
            builder.AppendLine(FormatMessageHeader(item.Message.Role));
            builder.AppendLine(item.Message.Content);
            builder.AppendLine();
        }
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

    private static string FormatRole(LlmMessageRole role)
    {
        return role.ToString().ToLowerInvariant();
    }

    private static string FormatSource(RuntimeContextSource source)
    {
        return source switch
        {
            RuntimeContextSource.StartupContext => "startup-context",
            RuntimeContextSource.RestoredConversationHistory => "restored-conversation-history",
            RuntimeContextSource.SessionTranscript => "session-transcript",
            RuntimeContextSource.CurrentTurnInput => "current-turn-input",
            _ => source.ToString().ToLowerInvariant()
        };
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    private static string FormatGraphNodes(IReadOnlyList<LoopGraphNodeDefinition> nodes)
    {
        if (nodes.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", nodes.Select(node => $"{node.Id}:{node.Kind}:{node.EditMode}"));
    }
}
