using System.Reflection;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal static class HumanReviewRecoveryRunnerTestFactory
{
    public static HumanReviewRecoveryRunner Create(
        HumanReviewRecoveryRecordingWorkRunner inner,
        HumanReviewRecoveryRecordingRunStore runs,
        HumanReviewRecoveryRecordingContinuationStore continuations,
        HumanReviewRecoveryRecordingDecisionActionStore actions,
        DateTimeOffset now,
        HumanReviewRecoveryRecordingPublicationService? publication = null,
        HumanReviewRecoveryReadinessSignal? readiness = null)
    {
        var clock = new HumanReviewRecoveryFixedClock(now);
        var constructor = typeof(HumanReviewRecoveryRunner).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 7);
        return (HumanReviewRecoveryRunner)constructor.Invoke([
            inner,
            runs,
            publication ?? new HumanReviewRecoveryRecordingPublicationService(),
            new HumanReviewContinuationRecoveryCoordinator(continuations, new HumanReviewRecoveryRecordingContinuationConsumer(), new HumanReviewRecoveryRecordingContinuationRelease(), clock),
            new HumanReviewDecisionActionRecoveryCoordinator(actions, new HumanReviewRecoveryRecordingDecisionActionConsumer(), new HumanReviewRecoveryRecordingDecisionActionRelease(), clock),
            new HumanReviewRecoveryRunnerOptions(8, "worker-a", "source-a", TimeSpan.FromMinutes(2)),
            readiness]);
    }
}
