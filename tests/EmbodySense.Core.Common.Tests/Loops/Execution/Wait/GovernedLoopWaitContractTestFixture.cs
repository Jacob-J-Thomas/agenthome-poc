using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Wait;

internal static class GovernedLoopWaitContractTestFixture
{
    internal static readonly DateTimeOffset DeadlineUtc = new DateTimeOffset(2026, 8, 13, 1, 2, 3, 456, TimeSpan.Zero).AddTicks(7_890);

    internal static string Hash(char value) => new(value, GovernedLoopWaitContractLimits.Sha256HexCharacters);

    internal static GovernedLoopNodeDescriptor TimestampDescriptor()
        => new(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.Timestamp, GovernedLoopWaitVocabulary.DescriptorVersion);

    internal static GovernedLoopNodeDescriptor EventDescriptor()
        => new(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.AuthenticatedEvent, GovernedLoopWaitVocabulary.DescriptorVersion);

    internal static IReadOnlyDictionary<string, string> TimestampParameters(DateTimeOffset? deadlineUtc = null)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = (deadlineUtc ?? DeadlineUtc).ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, System.Globalization.CultureInfo.InvariantCulture)
        };

    internal static IReadOnlyDictionary<string, string> EventParameters(string eventReference = "governed-event-1")
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GovernedLoopWaitVocabulary.EventReferenceParameter] = eventReference
        };

    internal static GovernedLoopWaitCondition TimestampCondition(DateTimeOffset? deadlineUtc = null)
    {
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(TimestampDescriptor(), TimestampParameters(deadlineUtc), out var condition, out var validation));
        Assert.True(validation.IsValid);
        return condition!;
    }

    internal static GovernedLoopWaitCondition EventCondition(string eventReference = "governed-event-1")
    {
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(EventDescriptor(), EventParameters(eventReference), out var condition, out var validation));
        Assert.True(validation.IsValid);
        return condition!;
    }

    internal static GovernedLoopWaitParkEvidence TimestampPark(long frontierVersion = 7)
    {
        var condition = TimestampCondition();
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(frontierVersion: frontierVersion),
            condition.WakeDeadlineUtc,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc);
        return GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(
            1,
            condition,
            checkpoint,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddTicks(-1),
            string.Empty));
    }

    internal static GovernedLoopWaitParkEvidence EventPark(string eventReference = "governed-event-1")
    {
        var condition = EventCondition(eventReference);
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint(eventReference);
        return GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(
            1,
            condition,
            checkpoint,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddTicks(-1),
            string.Empty));
    }

    internal static GovernedLoopWaitContinuationEvidence Continuation(
        GovernedLoopWaitParkEvidence? park = null,
        long preResumeFrontierVersion = 11,
        string? preResumeFrontierHash = null,
        long? resumedFrontierVersion = null,
        string? resumedFrontierHash = null)
    {
        var selectedPark = park ?? TimestampPark();
        var selectedResumedHash = resumedFrontierHash ?? Hash('f');
        var selectedPreResumeHash = preResumeFrontierHash
            ?? (preResumeFrontierVersion == selectedPark.Checkpoint.Binding.FrontierVersion
                ? selectedPark.Checkpoint.Binding.FrontierHash
                : Hash('e'));
        var preparedWake = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Prepared,
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(selectedPark.Checkpoint),
            recordedAtUtc: selectedPark.Checkpoint.WakeDeadlineUtc ?? selectedPark.Checkpoint.PublishedAtUtc.AddMinutes(1));
        return GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitContinuationEvidence(
            1,
            selectedPark.ContentHash,
            preparedWake,
            preResumeFrontierVersion,
            selectedPreResumeHash,
            resumedFrontierVersion ?? preResumeFrontierVersion + 1,
            selectedResumedHash,
            preparedWake.RecordedAtUtc.AddTicks(1),
            string.Empty));
    }

    internal static GovernedLoopWakeEvidence CommittedWake(
        GovernedLoopWaitContinuationEvidence continuation,
        GovernedLoopWakeIdentity? identity = null,
        string? continuationOperationId = null,
        string? continuationEvidenceHash = null,
        long? evidenceVersion = null,
        DateTimeOffset? recordedAtUtc = null)
        => GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            evidenceVersion ?? continuation.PreparedWakeEvidence.EvidenceVersion + 1,
            identity ?? continuation.PreparedWakeEvidence.Identity,
            continuationOperationId ?? continuation.PreparedWakeEvidence.ContinuationOperationId,
            continuationEvidenceHash ?? continuation.ContentHash,
            recordedAtUtc: recordedAtUtc ?? continuation.ResumedAtUtc.AddTicks(1));

    internal static GovernedLoopWaitContinuationEvidence WithPreparedWake(
        GovernedLoopWaitContinuationEvidence continuation,
        GovernedLoopWakeEvidence preparedWake)
        => GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitContinuationEvidence(
            continuation.SchemaVersion,
            continuation.ParkEvidenceHash,
            preparedWake,
            continuation.PreResumeFrontierVersion,
            continuation.PreResumeFrontierHash,
            continuation.ResumedFrontierVersion,
            continuation.ResumedFrontierHash,
            continuation.ResumedAtUtc,
            string.Empty));
}
