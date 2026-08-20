using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleRunAdmissionEvidenceContractTests
{
    private static readonly DateTimeOffset _recordedAtUtc = new(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ScheduleOverlapPolicy.Skip, ScheduleRunAdmissionDisposition.OverlapSkipped)]
    [InlineData(ScheduleOverlapPolicy.DeferOne, ScheduleRunAdmissionDisposition.OverlapDeferred)]
    [InlineData(ScheduleOverlapPolicy.DeferOne, ScheduleRunAdmissionDisposition.DeferredOneSuppressed)]
    [InlineData(ScheduleOverlapPolicy.Allow, ScheduleRunAdmissionDisposition.OverlapSerialized)]
    public void Policy_specific_evidence_is_hash_bound_bounded_and_immutable(
        ScheduleOverlapPolicy overlap,
        ScheduleRunAdmissionDisposition disposition)
    {
        var source = new[] { Attempt(1, disposition, disposition == ScheduleRunAdmissionDisposition.RunCreated ? null : "run-blocker") };
        var evidence = Evidence(overlap, source);
        source[0] = source[0] with { CandidateRunId = "run-substituted" };

        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(evidence));
        Assert.True(ScheduleRunAdmissionEvidenceHash.Matches(evidence));
        Assert.Equal("run-candidate", evidence.Attempts[0].CandidateRunId);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ScheduleRunAdmissionAttempt>)evidence.Attempts).Add(Attempt(2, disposition, "run-blocker")));
    }

    [Fact]
    public void Deferred_or_serialized_attempts_can_progress_to_one_created_run_with_stable_coordinates()
    {
        var deferred = Evidence(
            ScheduleOverlapPolicy.DeferOne,
            [
                Attempt(1, ScheduleRunAdmissionDisposition.OverlapDeferred, "run-blocker-a"),
                Attempt(2, ScheduleRunAdmissionDisposition.OverlapDeferred, "run-blocker-b", seconds: 1),
                Attempt(3, ScheduleRunAdmissionDisposition.RunCreated, null, seconds: 2),
            ]);
        var serialized = Evidence(
            ScheduleOverlapPolicy.Allow,
            [
                Attempt(1, ScheduleRunAdmissionDisposition.OverlapSerialized, "run-blocker", operationId: "invoke-serialized", runId: "run-serialized"),
                Attempt(2, ScheduleRunAdmissionDisposition.RunCreated, null, seconds: 1, operationId: "invoke-serialized", runId: "run-serialized"),
            ]);

        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(deferred));
        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(serialized));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("policy")]
    [InlineData("coordinates")]
    [InlineData("chronology")]
    [InlineData("terminal")]
    [InlineData("canonical-envelope")]
    public void Rehashed_substitution_and_illegal_append_sequences_still_fail_closed(string mutation)
    {
        var valid = Evidence(
            ScheduleOverlapPolicy.DeferOne,
            [Attempt(1, ScheduleRunAdmissionDisposition.OverlapDeferred, "run-blocker")]);
        var changed = mutation switch
        {
            "hash" => valid with { CanonicalEnvelopeHash = new string('0', 64) },
            "policy" => valid with
            {
                Attempts = [Attempt(1, ScheduleRunAdmissionDisposition.OverlapSerialized, "run-blocker")],
            },
            "coordinates" => valid with
            {
                Attempts =
                [
                    Attempt(1, ScheduleRunAdmissionDisposition.OverlapDeferred, "run-blocker"),
                    Attempt(2, ScheduleRunAdmissionDisposition.RunCreated, null, seconds: 1, runId: "run-other"),
                ],
            },
            "chronology" => valid with
            {
                Attempts =
                [
                    Attempt(1, ScheduleRunAdmissionDisposition.OverlapDeferred, "run-blocker", seconds: 2),
                    Attempt(2, ScheduleRunAdmissionDisposition.RunCreated, null, seconds: 1),
                ],
            },
            "terminal" => valid with
            {
                Attempts =
                [
                    Attempt(1, ScheduleRunAdmissionDisposition.DeferredOneSuppressed, "run-blocker"),
                    Attempt(2, ScheduleRunAdmissionDisposition.RunCreated, null, seconds: 1),
                ],
            },
            "canonical-envelope" => valid with { CanonicalEnvelope = valid.CanonicalEnvelope + " " },
            _ => throw new InvalidOperationException(mutation),
        };
        changed = ScheduleRunAdmissionEvidenceHash.Apply(changed);

        Assert.False(ScheduleRunAdmissionEvidenceValidator.IsValid(changed));
    }

    [Fact]
    public void Run_created_is_terminal_and_requires_no_blocker()
    {
        var valid = Evidence(ScheduleOverlapPolicy.Skip, [Attempt(1, ScheduleRunAdmissionDisposition.RunCreated, null)]);
        var blocked = ScheduleRunAdmissionEvidenceHash.Apply(valid with
        {
            Attempts = [Attempt(1, ScheduleRunAdmissionDisposition.RunCreated, "run-blocker")],
        });

        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(valid));
        Assert.False(ScheduleRunAdmissionEvidenceValidator.IsValid(blocked));
    }

    private static ScheduleRunAdmissionEvidence Evidence(
        ScheduleOverlapPolicy overlap,
        IReadOnlyList<ScheduleRunAdmissionAttempt> attempts)
    {
        var prepared = ScheduleContractTestData.Prepared(overlap: overlap);
        Assert.True(TriggerDeliveryJson.TrySerialize(prepared.Envelope, out var canonicalEnvelope, out _));
        Assert.True(TriggerDeliveryHash.TryCompute(prepared.Envelope, out var canonicalEnvelopeHash, out _));
        return ScheduleRunAdmissionEvidenceHash.Apply(new ScheduleRunAdmissionEvidence(
            ScheduleRunAdmissionEvidence.CurrentSchemaVersion,
            canonicalEnvelope!,
            canonicalEnvelopeHash!,
            prepared.Envelope.Loop.LoopId,
            attempts,
            string.Empty));
    }

    private static ScheduleRunAdmissionAttempt Attempt(
        int ordinal,
        ScheduleRunAdmissionDisposition disposition,
        string? blocker,
        int seconds = 0,
        string operationId = "invoke-candidate",
        string runId = "run-candidate")
        => new(
            ScheduleRunAdmissionAttempt.CurrentSchemaVersion,
            ordinal,
            disposition,
            operationId,
            runId,
            blocker,
            _recordedAtUtc.AddSeconds(seconds));
}
