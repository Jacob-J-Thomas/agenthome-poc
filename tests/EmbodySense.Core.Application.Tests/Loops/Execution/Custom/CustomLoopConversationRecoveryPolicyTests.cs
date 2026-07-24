using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopConversationRecoveryPolicyTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-23T12:00:00+00:00");

    [Fact]
    public void Requires_current_conversation_only_for_possible_remaining_publication()
    {
        var noPublication = Definition(
            [
                Step("first", publish: false),
                Step("second", publish: false)
            ],
            exitPublishes: false,
            maxAdditionalIterations: 0);
        var laterStepPublishes = noPublication with
        {
            InferenceSteps = [Step("first", publish: false), Step("second", publish: true)]
        };
        var earlierStepPublishesOnPossibleRepeat = noPublication with
        {
            InferenceSteps = [Step("first", publish: true), Step("second", publish: false)],
            ExitPolicy = noPublication.ExitPolicy with { MaxAdditionalIterations = 1 }
        };
        var exitPublishes = Definition([Step("first", publish: false)], exitPublishes: true, maxAdditionalIterations: 0);

        Assert.False(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(Run(noPublication, nextStepIndex: 0, bindConversation: false)));
        Assert.False(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(Run(noPublication, nextStepIndex: 0)));
        Assert.True(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(Run(laterStepPublishes, nextStepIndex: 1)));
        Assert.True(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(Run(earlierStepPublishesOnPossibleRepeat, nextStepIndex: 1)));
        Assert.True(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(Run(exitPublishes, nextStepIndex: 1)));
    }

    [Fact]
    public void Earlier_publication_does_not_retain_chat_after_last_possible_publication()
    {
        var definition = Definition(
            [
                Step("first", publish: true),
                Step("second", publish: false)
            ],
            exitPublishes: false,
            maxAdditionalIterations: 0);
        var run = Run(definition, nextStepIndex: 1);
        var priorPublication = Event(1, CustomLoopRunEventKind.ConversationPublished, iteration: 1, stepId: "first", published: true);
        run = run with { Events = [priorPublication] };

        Assert.False(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(run));
    }

    [Fact]
    public void Committed_exit_completion_proves_that_no_future_publication_can_run()
    {
        var definition = Definition([Step("first", publish: true)], exitPublishes: true, maxAdditionalIterations: 1);
        var completedExit = Event(1, CustomLoopRunEventKind.ExitDecisionCompleted, iteration: 1, stepId: "exit", exitDecision: CustomLoopExitDecision.Complete);
        var run = Run(definition, nextStepIndex: definition.InferenceSteps.Length) with
        {
            Events = [completedExit],
            Checkpoint = new CustomLoopRunCheckpoint(
                1,
                definition.InferenceSteps.Length,
                0,
                PendingExitDecision: false,
                [],
                null,
                new CustomLoopRetainedOutput("first", 1, "result", new string('a', 64)),
                0,
                completedExit.Sequence)
        };

        Assert.False(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(run));
    }

    [Fact]
    public void Nonpaused_runs_never_drive_startup_chat_preservation()
    {
        var definition = Definition([Step("first", publish: true)], exitPublishes: true, maxAdditionalIterations: 1);
        var run = Run(definition, nextStepIndex: 0) with { Status = CustomLoopRunStatus.NeedsReview };

        Assert.False(CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(run));
    }

    private static CustomLoopDefinition Definition(CustomLoopInferenceStep[] steps, bool exitPublishes, int maxAdditionalIterations)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-recovery-chat", "role-workspace", steps[0].Id, "create-loop", Timestamp);
        var definition = seed with
        {
            InferenceSteps = steps,
            ContextDefaults = new CustomLoopContextDefaults(Policy(publish: false), Policy(exitPublishes)),
            ExitPolicy = new CustomLoopExitPolicy(maxAdditionalIterations, CustomLoopDefinition.DefaultExitDecisionInstruction, CustomLoopNodeContextPolicy.Inherit())
        };
        return CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
    }

    private static CustomLoopInferenceStep Step(string id, bool publish)
    {
        return new CustomLoopInferenceStep(id, id, "Do the work.", CustomLoopNodeContextPolicy.Override(Policy(publish)));
    }

    private static CustomLoopContextPolicy Policy(bool publish)
    {
        return new CustomLoopContextPolicy(
            new CustomLoopContextInputPolicy(true, true, false, true, true),
            new CustomLoopContextOutputPolicy(false, publish));
    }

    private static CustomLoopRunRecord Run(CustomLoopDefinition definition, int nextStepIndex, bool bindConversation = true)
    {
        var conversation = bindConversation
            ? new CustomLoopConversationReference("conversation-current", new string('b', 64), Timestamp)
            : null;
        return new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-recovery-chat",
            definition.Id,
            1,
            CustomLoopRunStatus.Paused,
            Timestamp,
            Timestamp,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "invoke-recovery-chat",
            AuditSchema.Actors.Web,
            new string('c', 64),
            definition,
            "prompt",
            conversation,
            CustomLoopContextSnapshot.CreateEmpty(Timestamp),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start() with { NextStepIndex = nextStepIndex },
            [],
            null,
            null,
            null);
    }

    private static CustomLoopRunEvent Event(
        long sequence,
        CustomLoopRunEventKind kind,
        int? iteration = null,
        string? stepId = null,
        bool? published = null,
        CustomLoopExitDecision? exitDecision = null)
    {
        return new CustomLoopRunEvent(
            sequence,
            $"event-{sequence}",
            Timestamp,
            kind,
            iteration,
            stepId,
            1,
            "evidence",
            [],
            null,
            null,
            null,
            null,
            published,
            null,
            null,
            null,
            null,
            exitDecision);
    }
}
