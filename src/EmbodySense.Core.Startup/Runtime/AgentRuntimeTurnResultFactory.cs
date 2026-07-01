using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

internal static class AgentRuntimeTurnResultFactory
{
    public static AgentRuntimeTurnResult FromCommand(RuntimeCommandResult result)
    {
        if (result.ExitRequested)
        {
            return AgentRuntimeTurnResult.Exit();
        }

        var restoredMessages = result.RestoredMessages.Select(message => new AgentRuntimeTranscriptMessage(FormatRole(message.Role), message.Content)).ToArray();
        return AgentRuntimeTurnResult.CommandOutput(result.Output, result.Prompt, result.AwaitingInput, restoredMessages, result.ReplaceTranscript);
    }

    public static AgentRuntimeTurnResult FromDefaultLoop(DefaultConversationLoopTurnResult result)
    {
        var runIdentity = ToRuntimeRunIdentity(result.RunIdentity);
        return result.Status switch
        {
            DefaultConversationLoopTurnStatus.Completed => AgentRuntimeTurnResult.MessageCompleted(result.AssistantOutput, runIdentity),
            DefaultConversationLoopTurnStatus.Cancelled => AgentRuntimeTurnResult.MessageCancelled(result.FailureDetail ?? "Turn was cancelled.", runIdentity),
            DefaultConversationLoopTurnStatus.Failed => AgentRuntimeTurnResult.MessageFailed(
                result.FailureDetail ?? "Default conversation loop turn failed.",
                runIdentity,
                BuildAcceptedAssistantEvents(result.TranscriptMessages, runIdentity)),
            _ => throw new InvalidOperationException($"Unsupported default conversation loop status: {result.Status}.")
        };
    }

    private static IReadOnlyList<AgentRuntimeTurnEvent> BuildAcceptedAssistantEvents(
        IReadOnlyList<RuntimeTranscriptMessage> transcriptMessages,
        AgentRuntimeRunIdentity? runIdentity)
    {
        return transcriptMessages
            .Where(message => message.Role == LlmMessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => AgentRuntimeTurnEvent.AssistantMessage(message.Content, runIdentity))
            .ToArray();
    }

    private static AgentRuntimeRunIdentity? ToRuntimeRunIdentity(LoopRunIdentity? runIdentity)
    {
        return runIdentity is null
            ? null
            : new AgentRuntimeRunIdentity(runIdentity.LoopId, runIdentity.RunId, runIdentity.RoleId);
    }

    private static string FormatRole(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "system",
            LlmMessageRole.User => "user",
            LlmMessageRole.Assistant => "assistant",
            LlmMessageRole.Tool => "tool",
            _ => role.ToString().ToLowerInvariant()
        };
    }
}
