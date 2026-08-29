using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.HumanInput;

namespace EmbodySense.Core.Application.Tests.Loops.Wait;

/// <summary>Exercises the authenticated-event namespace routing boundary for the generic wait relay.</summary>
public sealed class GovernedLoopWaitContinuationRelayTests
{
    [Fact]
    public async Task Reserved_human_input_reference_routes_only_to_the_human_input_target_and_fails_closed_when_unbound()
    {
        var relay = new GovernedLoopWaitContinuationRelay();
        var defaultTarget = new StubGovernedLoopWakeContinuationPort();
        var humanInputTarget = new StubGovernedLoopWakeContinuationPort();
        relay.Bind(defaultTarget);
        var request = Request(GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix + "checkpoint-one");

        var unbound = await relay.ContinueAsync(request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, unbound!.Status);
        Assert.Equal(0, defaultTarget.ContinueCount);
        relay.BindHumanInput(humanInputTarget);

        var routed = await relay.ContinueAsync(request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, routed!.Status);
        Assert.Equal(0, defaultTarget.ContinueCount);
        Assert.Equal(1, humanInputTarget.ContinueCount);
        Assert.Throws<InvalidOperationException>(() => relay.BindHumanInput(new StubGovernedLoopWakeContinuationPort()));
    }

    [Fact]
    public async Task Nonreserved_event_reference_stays_with_the_default_target()
    {
        var relay = new GovernedLoopWaitContinuationRelay();
        var defaultTarget = new StubGovernedLoopWakeContinuationPort();
        var humanInputTarget = new StubGovernedLoopWakeContinuationPort();
        relay.Bind(defaultTarget);
        relay.BindHumanInput(humanInputTarget);

        var result = await relay.ReconcileAsync(Request("ordinary-authenticated-event"));

        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, result!.Status);
        Assert.Equal(1, defaultTarget.ReconcileCount);
        Assert.Equal(0, humanInputTarget.ReconcileCount);
    }

    private static GovernedLoopWakeContinuationRequest Request(string eventReference)
    {
        var publication = GovernedLoopSleepApplicationTestFixture.PublicationRequest(
            GovernedLoopSleepApplicationTestFixture.Posture(),
            GovernedLoopWakeMode.AuthenticatedEvent,
            eventReference: eventReference);
        var checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
            string.Empty,
            publication.Binding,
            publication.WakeMode,
            publication.WakeDeadlineUtc,
            publication.AuthenticatedEventReference,
            publication.CheckpointPreparedAtUtc ?? GovernedLoopSleepApplicationTestFixture.Now,
            string.Empty));
        var identity = GovernedLoopSleepApplicationTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        return new GovernedLoopWakeContinuationRequest(
            checkpoint,
            identity,
            prepared.ContinuationOperationId!,
            prepared,
            GovernedLoopSleepApplicationTestFixture.Hash('9'));
    }
}
