using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Persistence.Tests.Triggers.Schedules;

internal static class ScheduleStoreTestData
{
    internal static (ScheduleStoreCreateRequest Request, TriggerDeliveryEnvelope Envelope) ProvenanceRequest(
        ScheduleDeliveryResultKind resultKind = ScheduleDeliveryResultKind.Queued)
    {
        var definition = ScheduleContractTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var hashValidation), ScheduleContractTestData.Errors(hashValidation));
        var occurrence = ScheduleContractTestData.Occurrence();
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash!,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var identity = ScheduleContractTestData.Identity(
            occurrence,
            definitionHash!,
            definition.Revision,
            definition.ScheduleId);
        var result = ScheduleContractTestData.Result(prepared.CanonicalEnvelopeHash, resultKind);
        var terminal = new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            occurrence,
            identity,
            new string('f', ScheduleContractLimits.Sha256HexCharacters),
            new string('9', ScheduleContractLimits.Sha256HexCharacters),
            new string('8', ScheduleContractLimits.Sha256HexCharacters),
            result,
            result.RecordedAtUtc.AddSeconds(1));
        var state = ScheduleContractTestData.State(
            ScheduleContractTestData.OccurrenceAt(2),
            definitionRevision: definition.Revision,
            definitionHash: definitionHash!,
            terminal: [terminal],
            scheduleId: definition.ScheduleId);
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid);
        return (new ScheduleStoreCreateRequest(definition, state, definitionHash!), prepared.Envelope);
    }

    internal static ScheduleStoreCreateRequest CreateRequest(
        string scheduleId = "daily-reflection",
        long stateRevision = 1,
        bool comprehensiveState = false)
    {
        Assert.True(ScheduleId.TryParse(scheduleId, out var parsedScheduleId));
        var definition = ScheduleContractTestData.Definition() with
        {
            ScheduleId = parsedScheduleId!,
            Payload = new SchedulePayloadReference(
                $"payload/{scheduleId}",
                ScheduleContractTestData.Definition().Payload.ContentHash),
        };
        Assert.True(
            ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var hashValidation),
            ScheduleContractTestData.Errors(hashValidation));
        var state = comprehensiveState
            ? ComprehensiveState(definition, definitionHash!, stateRevision)
            : ScheduleContractTestData.State(
                definitionRevision: definition.Revision,
                definitionHash: definitionHash!,
                stateRevision: stateRevision,
                scheduleId: definition.ScheduleId);
        var composition = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state);
        Assert.True(composition.IsValid, ScheduleContractTestData.Errors(composition));
        return new ScheduleStoreCreateRequest(definition, state, definitionHash!);
    }

    internal static ScheduleState Replacement(ScheduleState state, int variant = 1)
    {
        var replacement = state with
        {
            StateRevision = state.StateRevision + 1,
            LastClockObservedAtUtc = state.LastClockObservedAtUtc!.Value.AddSeconds(variant),
        };
        Assert.True(ScheduleContractValidator.ValidateState(replacement).IsValid);
        return replacement;
    }

    private static ScheduleState ComprehensiveState(
        ScheduleDefinition definition,
        string definitionHash,
        long stateRevision)
    {
        var occurrence = ScheduleContractTestData.OccurrenceAt(3);
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            prepared,
            ScheduleContractTestData.Result(
                prepared.CanonicalEnvelopeHash,
                recordedAtUtc: occurrence.ScheduledAtUtc.AddSeconds(4)),
            definitionHash: definitionHash,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var terminalOccurrence = ScheduleContractTestData.OccurrenceAt(2);
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            definitionHash,
            terminalOccurrence,
            out var terminalIdentity,
            out var identityValidation), ScheduleContractTestData.Errors(identityValidation));
        var terminal = new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            terminalOccurrence,
            terminalIdentity!,
            new string('f', ScheduleContractLimits.Sha256HexCharacters),
            new string('9', ScheduleContractLimits.Sha256HexCharacters),
            new string('8', ScheduleContractLimits.Sha256HexCharacters),
            ScheduleContractTestData.Result(
                new string('7', ScheduleContractLimits.Sha256HexCharacters),
                recordedAtUtc: terminalOccurrence.ScheduledAtUtc.AddSeconds(1)),
            terminalOccurrence.ScheduledAtUtc.AddSeconds(2));
        return ScheduleContractTestData.State(
            occurrence,
            pending,
            [ScheduleContractTestData.Disposition(1, disposition: ScheduleOccurrenceDisposition.OverlapDeferred)],
            definition.Revision,
            definitionHash,
            stateRevision,
            terminal: [terminal],
            scheduleId: definition.ScheduleId);
    }
}
