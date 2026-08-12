using System.Text.Json;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleExecutionDirectiveContractTests
{
    [Fact]
    public void Valid_directive_binds_every_coordinate_into_a_stable_hash()
    {
        var baseline = Directive();
        Assert.True(ScheduleContractHash.TryComputeExecutionDirective(
            baseline,
            out var baselineHash,
            out var validation),
            ScheduleContractTestData.Errors(validation));
        Assert.Matches("^[0-9a-f]{64}$", baselineHash!);

        AssertHashChanged(baselineHash!, Directive(scheduleIdText: "another-schedule"));
        AssertHashChanged(baselineHash!, Directive(definitionRevision: 2));
        AssertHashChanged(baselineHash!, Directive(definitionHash: new string('1', 64)));
        AssertHashChanged(baselineHash!, Directive(occurrence: ScheduleContractTestData.OccurrenceAt(2)));
        AssertHashChanged(
            baselineHash!,
            Directive(target: TriggerDeliveryTestData.GovernedLoop(graphId: "another-graph")));
        AssertHashChanged(baselineHash!, Directive(overlap: ScheduleOverlapPolicy.Allow));
        AssertHashChanged(
            baselineHash!,
            Directive(preQueueOverlapEvidenceHash: new string('2', 64)));
    }

    [Fact]
    public void Validator_rejects_malformed_bounded_and_nonderived_coordinates()
    {
        Assert.False(ScheduleContractValidator.ValidateExecutionDirective(null).IsValid);
        var directive = Directive();
        AssertInvalid(directive with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertInvalid(
            directive with { DefinitionRevision = ScheduleContractLimits.MaxRevision + 1 },
            "definitionRevision",
            "revision_out_of_range");
        AssertInvalid(directive with { DefinitionHash = new string('A', 64) }, "definitionHash", "invalid_hash");
        AssertInvalid(
            directive with { Occurrence = directive.Occurrence with { Ordinal = 0 } },
            "occurrence.ordinal",
            "ordinal_out_of_range");
        AssertInvalid(
            directive with { Identity = ScheduleContractTestData.Identity(ScheduleContractTestData.OccurrenceAt(2)) },
            "identity",
            "identity_derivation_mismatch");
        var legacyTarget = directive with { Target = TriggerDeliveryTestData.Loop() };
        AssertInvalid(
            legacyTarget,
            "target",
            "governed_target_required");
        AssertScheduledRejected(
            legacyTarget,
            Inputs(directive),
            expectedCode: "governed_target_required");
        AssertInvalid(
            directive with { Overlap = ScheduleOverlapPolicy.Unknown },
            "overlap",
            "unsupported_overlap_policy");
        AssertInvalid(
            directive with { PreQueueOverlapEvidenceHash = new string('A', 64) },
            "preQueueOverlapEvidenceHash",
            "invalid_hash");
        AssertScheduledRejected(
            directive with { Occurrence = null! },
            Inputs(directive),
            expectedCode: "required");
        AssertScheduledRejected(
            directive with { Target = null! },
            Inputs(directive),
            expectedCode: "required");

        Assert.True(ScheduleId.TryParse(
            new string('a', ScheduleContractLimits.MaxScheduleIdCharacters),
            out var maximumScheduleId));
        var boundary = Directive(
            scheduleIdText: maximumScheduleId!.Value,
            definitionRevision: ScheduleContractLimits.MaxRevision);
        Assert.True(ScheduleContractValidator.ValidateExecutionDirective(boundary).IsValid);
        Assert.False(ScheduleId.TryParse(
            new string('a', ScheduleContractLimits.MaxScheduleIdCharacters + 1),
            out _));
    }

    [Fact]
    public void Time_envelopes_require_a_directive_and_other_kinds_forbid_it()
    {
        var directive = Directive();
        var inputs = Inputs(directive);
        Assert.False(TriggerDeliveryFactory.TryCreateEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            directive.Identity.DeliveryId,
            directive.Identity.DeduplicationId,
            TriggerKind.Time,
            inputs.Adapter,
            directive.Target,
            inputs.Actor,
            inputs.Authority,
            inputs.Temporal,
            inputs.Payload,
            inputs.Redelivery,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out _,
            out var missingValidation));
        Assert.Contains(
            missingValidation.Errors,
            error => error.Field == "scheduleExecutionDirective"
                && error.Code == "schedule_execution_directive_required");

        var scheduled = Envelope(directive);
        Assert.True(TriggerDeliveryJson.TrySerialize(scheduled, out var scheduledJson, out _));
        var forgedOtherKind = scheduledJson!.Replace(
            "\"kind\":\"time\"",
            "\"kind\":\"webhook\"",
            StringComparison.Ordinal);
        Assert.False(TriggerDeliveryJson.TryDeserialize(
            forgedOtherKind,
            out _,
            out var forbiddenValidation));
        Assert.Contains(
            forbiddenValidation.Errors,
            error => error.Field == "scheduleExecutionDirective"
                && error.Code == "schedule_execution_directive_forbidden");

        Assert.True(TriggerDeliveryJson.TrySerialize(
            TriggerDeliveryTestData.Envelope(),
            out var nonscheduledJson,
            out _));
        var forgedTime = nonscheduledJson!.Replace(
            "\"kind\":\"webhook\"",
            "\"kind\":\"time\"",
            StringComparison.Ordinal);
        Assert.False(TriggerDeliveryJson.TryDeserialize(
            forgedTime,
            out _,
            out var requiredValidation));
        Assert.Contains(
            requiredValidation.Errors,
            error => error.Field == "scheduleExecutionDirective"
                && error.Code == "schedule_execution_directive_required");
    }

    [Fact]
    public void Scheduled_factory_rejects_target_identity_and_occurrence_mismatches()
    {
        var directive = Directive();
        var inputs = Inputs(directive);
        AssertScheduledRejected(
            directive,
            inputs,
            loop: TriggerDeliveryTestData.GovernedLoop(graphId: "other-target"),
            expectedCode: "schedule_target_mismatch");

        Assert.True(TriggerDeliveryId.TryParse("other-delivery", out var otherDelivery));
        AssertScheduledRejected(
            directive,
            inputs,
            deliveryId: otherDelivery,
            expectedCode: "schedule_identity_mismatch");

        var wrongTemporal = TriggerDeliveryTestData.Temporal(
            createdAtUtc: directive.Occurrence.ScheduledAtUtc.AddSeconds(1),
            observedAtUtc: directive.Occurrence.ScheduledAtUtc.AddSeconds(2),
            receivedAtUtc: directive.Occurrence.ScheduledAtUtc.AddSeconds(3));
        AssertScheduledRejected(
            directive,
            inputs with { Temporal = wrongTemporal },
            expectedCode: "schedule_occurrence_mismatch");
    }

    [Fact]
    public void Canonical_json_round_trips_with_one_exact_directive_property_set()
    {
        var envelope = Envelope(Directive());
        Assert.True(TriggerDeliveryJson.TrySerialize(envelope, out var json, out _));
        Assert.True(TriggerDeliveryJson.TryDeserialize(json, out var parsed, out var validation));
        Assert.True(validation.IsValid);
        Assert.Equal(envelope.ScheduleExecutionDirective, parsed!.ScheduleExecutionDirective);

        using var document = JsonDocument.Parse(json!);
        var rootNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("scheduleExecutionDirective", rootNames);
        var directiveNames = document.RootElement
            .GetProperty("scheduleExecutionDirective")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(
            [
                "definitionHash",
                "definitionRevision",
                "identity",
                "occurrence",
                "overlap",
                "preQueueOverlapEvidenceHash",
                "scheduleId",
                "schemaVersion",
                "target",
            ],
            directiveNames);

        var extraProperty = json!.Replace(
            "\"definitionHash\":",
            "\"unexpected\":null,\"definitionHash\":",
            StringComparison.Ordinal);
        Assert.False(TriggerDeliveryJson.TryDeserialize(extraProperty, out _, out var extraValidation));
        Assert.Contains(extraValidation.Errors, error => error.Code == "invalid_json_shape");

        var reordered = json.Replace(
            "\"definitionHash\":\"" + new string('d', 64) + "\",\"definitionRevision\":1",
            "\"definitionRevision\":1,\"definitionHash\":\"" + new string('d', 64) + "\"",
            StringComparison.Ordinal);
        Assert.False(TriggerDeliveryJson.TryDeserialize(reordered, out _, out var reorderedValidation));
        Assert.Contains(reorderedValidation.Errors, error => error.Code == "noncanonical_json");
    }

    [Fact]
    public void Envelope_defensively_copies_the_complete_schedule_directive()
    {
        var directive = Directive();
        var envelope = Envelope(directive);
        var snapshot = envelope.ScheduleExecutionDirective!;

        Assert.NotSame(directive, snapshot);
        Assert.NotSame(directive.Occurrence, snapshot.Occurrence);
        Assert.NotSame(directive.Occurrence.TimeZone, snapshot.Occurrence.TimeZone);
        Assert.NotSame(directive.Identity, snapshot.Identity);
        Assert.NotSame(directive.Target, snapshot.Target);
        Assert.NotSame(directive.Target.GovernedPublication, snapshot.Target.GovernedPublication);
        Assert.NotSame(directive.Target.AuthorityGrant, snapshot.Target.AuthorityGrant);
        Assert.Equal(directive, snapshot);
    }

    [Fact]
    public void Envelope_hash_transitively_covers_schedule_directive_evidence()
    {
        var baseline = Envelope(Directive());
        var changed = Envelope(Directive(preQueueOverlapEvidenceHash: new string('2', 64)));
        Assert.True(TriggerDeliveryHash.TryCompute(baseline, out var baselineHash, out _));
        Assert.True(TriggerDeliveryHash.TryCompute(changed, out var changedHash, out _));
        Assert.NotEqual(baselineHash, changedHash);
    }

    private static ScheduleExecutionDirective Directive(
        string scheduleIdText = "daily-reflection",
        long definitionRevision = 1,
        string? definitionHash = null,
        ScheduleOccurrence? occurrence = null,
        TriggerLoopReference? target = null,
        ScheduleOverlapPolicy overlap = ScheduleOverlapPolicy.DeferOne,
        string? preQueueOverlapEvidenceHash = null)
    {
        Assert.True(ScheduleId.TryParse(scheduleIdText, out var scheduleId));
        return TriggerDeliveryTestData.ScheduleDirective(
            occurrence ?? ScheduleContractTestData.Occurrence(),
            scheduleId,
            definitionRevision,
            definitionHash ?? ScheduleContractTestData.DefinitionHash,
            target ?? ScheduleContractTestData.Target(),
            overlap,
            preQueueOverlapEvidenceHash ?? new string('8', 64));
    }

    private static TriggerDeliveryEnvelope Envelope(ScheduleExecutionDirective directive)
    {
        var inputs = Inputs(directive);
        Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            directive.Identity.DeliveryId,
            directive.Identity.DeduplicationId,
            inputs.Adapter,
            directive.Target,
            inputs.Actor,
            inputs.Authority,
            inputs.Temporal,
            inputs.Payload,
            inputs.Redelivery,
            directive,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var envelope,
            out var validation),
            string.Join(',', validation.Errors.Select(error => $"{error.Field}:{error.Code}")));
        return envelope!;
    }

    private static FactoryInputs Inputs(ScheduleExecutionDirective directive)
    {
        var created = directive.Occurrence.ScheduledAtUtc;
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(
            1,
            1,
            directive.Identity.DeliveryId,
            out var redelivery,
            out _));
        return new FactoryInputs(
            TriggerDeliveryTestData.Adapter(
                "org.embodysense/triggers/time",
                implementation: "triggers/time"),
            TriggerDeliveryTestData.ActorContext(surface: "scheduler"),
            TriggerDeliveryTestData.Authority(evaluatedAtUtc: created.AddSeconds(2)),
            TriggerDeliveryTestData.Temporal(
                createdAtUtc: created,
                observedAtUtc: created.AddSeconds(1),
                receivedAtUtc: created.AddSeconds(2)),
            TriggerDeliveryTestData.InlinePayload([1, 2, 3]),
            redelivery!);
    }

    private static void AssertScheduledRejected(
        ScheduleExecutionDirective directive,
        FactoryInputs inputs,
        TriggerLoopReference? loop = null,
        TriggerDeliveryId? deliveryId = null,
        string expectedCode = "invalid_schedule_execution_directive")
    {
        Assert.False(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            deliveryId ?? directive.Identity.DeliveryId,
            directive.Identity.DeduplicationId,
            inputs.Adapter,
            loop ?? directive.Target,
            inputs.Actor,
            inputs.Authority,
            inputs.Temporal,
            inputs.Payload,
            inputs.Redelivery,
            directive,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out _,
            out var validation));
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private static void AssertInvalid(
        ScheduleExecutionDirective directive,
        string path,
        string code)
    {
        var validation = ScheduleContractValidator.ValidateExecutionDirective(directive);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
        Assert.False(ScheduleContractHash.TryComputeExecutionDirective(directive, out _, out _));
    }

    private static void AssertHashChanged(string baselineHash, ScheduleExecutionDirective directive)
    {
        Assert.True(ScheduleContractHash.TryComputeExecutionDirective(directive, out var hash, out _));
        Assert.NotEqual(baselineHash, hash);
    }

    private sealed record FactoryInputs(
        TriggerAdapterReference Adapter,
        TriggerActorContext Actor,
        TriggerAuthorityEvidence Authority,
        TriggerTemporalEvidence Temporal,
        TriggerPayloadEvidence Payload,
        TriggerRedeliveryEvidence Redelivery);
}
