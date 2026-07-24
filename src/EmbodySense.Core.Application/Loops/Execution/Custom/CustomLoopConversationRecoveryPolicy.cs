using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public static class CustomLoopConversationRecoveryPolicy
{
    public static bool RequiresCurrentConversation(CustomLoopRunRecord? run)
    {
        if (run is not { Status: CustomLoopRunStatus.Paused, InvokingConversation: not null })
        {
            return false;
        }

        try
        {
            var definition = run.AdmittedDefinition;
            var checkpoint = run.Checkpoint;
            var stepCount = definition.InferenceSteps.Length;
            if (checkpoint.NextStepIndex < 0 || checkpoint.NextStepIndex > stepCount)
            {
                return true;
            }

            if (HasCommittedExitCompletion(run))
            {
                return false;
            }

            if (definition.InferenceSteps
                .Skip(checkpoint.NextStepIndex)
                .Any(step => Publishes(step.ContextPolicy, definition.ContextDefaults.Inference)))
            {
                return true;
            }

            if (Publishes(definition.ExitPolicy.ContextPolicy, definition.ContextDefaults.Exit))
            {
                return true;
            }

            var repeatMayBeAccepted = checkpoint.AcceptedRepeatCount < definition.ExitPolicy.MaxAdditionalIterations;
            return repeatMayBeAccepted
                && definition.InferenceSteps.Any(step => Publishes(step.ContextPolicy, definition.ContextDefaults.Inference));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            // Recovery must retain user evidence when corrupt or future policy shapes
            // prevent proving that the exact invoking conversation is no longer needed.
            return true;
        }
    }

    private static bool Publishes(CustomLoopNodeContextPolicy configured, CustomLoopContextPolicy defaults)
    {
        return CustomLoopContextResolver.ResolvePolicy(configured, defaults).ContextOut.PublishToInvokingConversation;
    }

    private static bool HasCommittedExitCompletion(CustomLoopRunRecord run)
    {
        return !run.Checkpoint.PendingExitDecision
            && run.Checkpoint.NextStepIndex == run.AdmittedDefinition.InferenceSteps.Length
            && run.Checkpoint.CurrentIterationResult is not null
            && run.Events.Any(item => item.Sequence <= run.Checkpoint.LastCommittedSequence
                && item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted
                && item.Iteration == run.Checkpoint.Iteration);
    }
}
