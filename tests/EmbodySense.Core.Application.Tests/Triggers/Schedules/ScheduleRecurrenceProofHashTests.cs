using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Triggers.Schedules;

public sealed class ScheduleRecurrenceProofHashTests
{
    [Fact]
    public void Deferred_occurrence_proof_is_deterministic_and_binds_its_durable_coordinates()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var current = ScheduleEvaluatorTestData.Occurrence(timeZone: definition.TimeZone);
        var deferredOccurrence = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 2,
            local: current.ScheduledLocal.AddDays(1),
            utc: current.ScheduledAtUtc.AddDays(1),
            timeZone: definition.TimeZone);
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            definitionHash!,
            deferredOccurrence,
            out var identity,
            out _));
        var deferredAtUtc = deferredOccurrence.ScheduledAtUtc.AddMinutes(1);
        var deferred = new ScheduleDeferredOccurrence(
            ScheduleDeferredOccurrence.CurrentSchemaVersion,
            deferredOccurrence,
            identity!,
            deferredAtUtc);
        var disposition = DeferredDisposition(deferredOccurrence, deferredAtUtc);
        var plan = new ScheduleFinalizationPlan(
            ScheduleFinalizationPlan.CurrentSchemaVersion,
            deferredOccurrence,
            null,
            deferred,
            [disposition]);

        var first = ScheduleRecurrenceProofHash.Compute(definitionHash!, current, plan, [new string('1', 64)]);
        var replay = ScheduleRecurrenceProofHash.Compute(definitionHash!, current, plan, [new string('1', 64)]);
        var laterDeferral = ScheduleRecurrenceProofHash.Compute(
            definitionHash!,
            current,
            plan with { DeferredOccurrence = deferred with { DeferredAtUtc = deferredAtUtc.AddSeconds(1) } },
            [new string('1', 64)]);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, laterDeferral);
        AssertSha256(first);
    }

    [Fact]
    public void Invalid_local_time_disposition_hashes_nullable_utc_fields_and_all_resolution_evidence_in_order()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var current = ScheduleEvaluatorTestData.Occurrence(timeZone: definition.TimeZone);
        var skippedLocal = current.ScheduledLocal.AddDays(1);
        var skipped = new ScheduleOccurrenceDispositionEvidence(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            2,
            2,
            1,
            skippedLocal,
            skippedLocal,
            null,
            null,
            definition.TimeZone,
            ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped,
            null,
            "invalid-local-time-skipped",
            current.ScheduledAtUtc.AddMinutes(1));
        var next = ScheduleEvaluatorTestData.Occurrence(
            ordinal: 3,
            local: skippedLocal.AddDays(1),
            utc: current.ScheduledAtUtc.AddDays(2),
            timeZone: definition.TimeZone);
        var plan = new ScheduleFinalizationPlan(1, next, null, null, [skipped]);
        var firstEvidence = new string('1', 64);
        var secondEvidence = new string('2', 64);

        var first = ScheduleRecurrenceProofHash.Compute(definitionHash!, current, plan, [firstEvidence, secondEvidence]);
        var replay = ScheduleRecurrenceProofHash.Compute(definitionHash!, current, plan, [firstEvidence, secondEvidence]);
        var reversed = ScheduleRecurrenceProofHash.Compute(definitionHash!, current, plan, [secondEvidence, firstEvidence]);
        var changedReason = ScheduleRecurrenceProofHash.Compute(
            definitionHash!,
            current,
            plan with { DispositionEvidence = [skipped with { ReasonCode = "dst-gap-skipped" }] },
            [firstEvidence, secondEvidence]);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, reversed);
        Assert.NotEqual(first, changedReason);
    }

    [Fact]
    public void Recurrence_proof_rejects_each_unbounded_or_malformed_top_level_input()
    {
        var definition = ScheduleEvaluatorTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var occurrence = ScheduleEvaluatorTestData.Occurrence(timeZone: definition.TimeZone);
        var plan = new ScheduleFinalizationPlan(1, null, null, null, []);
        var tooManyEvidenceHashes = Enumerable
            .Repeat(new string('1', 64), ScheduleContractLimits.MaxFinalizationEvidenceItems + 2)
            .ToArray();

        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.Compute(
            definitionHash!.ToUpperInvariant(), occurrence, plan, []));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.Compute(
            definitionHash!, occurrence with { Ordinal = 0 }, plan, []));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.Compute(
            definitionHash!, occurrence, plan with { SchemaVersion = 2 }, []));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.Compute(
            definitionHash!, occurrence, plan, tooManyEvidenceHashes));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.Compute(
            definitionHash!, occurrence, plan, [new string('F', 64)]));
    }

    [Fact]
    public void Time_zone_resolution_proofs_bind_nullable_fields_and_enforce_identifier_and_hash_bounds()
    {
        var occurrence = ScheduleEvaluatorTestData.Occurrence();
        var unavailable = new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unavailable,
            null,
            occurrence.ScheduledLocal,
            null,
            null);
        var resolved = new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            occurrence.TimeZone.RulesFingerprint,
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            null);

        var unavailableHash = ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone, occurrence.ScheduledLocal, unavailable);
        var replay = ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone, occurrence.ScheduledLocal, unavailable);
        var resolvedHash = ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone, occurrence.ScheduledLocal, resolved);
        var instantHash = ScheduleRecurrenceProofHash.ComputeInstantResolution(
            occurrence.TimeZone,
            occurrence.ScheduledAtUtc,
            new ScheduleInstantResolution(
                ScheduleInstantResolutionStatus.Resolved,
                occurrence.TimeZone.RulesFingerprint,
                occurrence.ScheduledLocal));

        Assert.Equal(unavailableHash, replay);
        Assert.NotEqual(unavailableHash, resolvedHash);
        Assert.NotEqual(resolvedHash, instantHash);
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone with { TimeZoneId = new string('z', ScheduleContractLimits.MaxTimeZoneIdCharacters + 1) },
            occurrence.ScheduledLocal,
            unavailable));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.ComputeInstantResolution(
            occurrence.TimeZone with { RulesFingerprint = new string('F', 64) },
            occurrence.ScheduledAtUtc,
            new ScheduleInstantResolution(ScheduleInstantResolutionStatus.Unavailable, null, occurrence.ScheduledLocal)));
        Assert.Throws<ArgumentException>(() => ScheduleRecurrenceProofHash.ComputeLocalResolution(
            occurrence.TimeZone,
            occurrence.ScheduledLocal,
            resolved with { RulesFingerprint = new string('F', 64) }));
    }

    private static ScheduleOccurrenceDispositionEvidence DeferredDisposition(
        ScheduleOccurrence occurrence,
        DateTimeOffset recordedAtUtc)
        => new(
            ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
            occurrence.Ordinal,
            occurrence.Ordinal,
            1,
            occurrence.ScheduledLocal,
            occurrence.ScheduledLocal,
            occurrence.ScheduledAtUtc,
            occurrence.ScheduledAtUtc,
            occurrence.TimeZone,
            ScheduleOccurrenceDisposition.OverlapDeferred,
            new string('a', 64),
            "overlap-policy-defer-one",
            recordedAtUtc);

    private static void AssertSha256(string value)
    {
        Assert.Equal(ScheduleContractLimits.Sha256HexCharacters, value.Length);
        Assert.All(value, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
