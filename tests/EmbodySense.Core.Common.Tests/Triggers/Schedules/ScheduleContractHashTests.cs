using System.Globalization;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleContractHashTests
{
    [Fact]
    public void Definition_hash_is_canonical_culture_independent_and_pinned()
    {
        var definition = ScheduleContractTestData.Definition();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var localized = DefinitionHash(definition);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal(localized, DefinitionHash(definition));
            Assert.Equal("c83c7a9b6e888bcb28f72d963721cb5874c6582e77ce265df1731fed6ca4cec6", localized);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Every_definition_contract_slice_changes_canonical_identity()
    {
        var definition = ScheduleContractTestData.Definition();
        var expected = DefinitionHash(definition);
        var variants = new[]
        {
            definition with { Revision = 2 },
            definition with { Target = TriggerDeliveryTestData.GovernedLoop(graphId: "another-loop") },
            definition with { TimeAdapter = TriggerDeliveryTestData.Adapter("org.embodysense/triggers/clock", implementation: "triggers/clock") },
            definition with { SurfaceId = "schedule-service" },
            definition with { WorkspaceId = "workspace-2" },
            definition with { RoleId = "scheduler" },
            definition with { Payload = definition.Payload with { GovernedReference = "payload/another" } },
            definition with { Priority = SchedulePriority.Critical },
            definition with { Recurrence = definition.Recurrence with { FirstLocalOccurrence = definition.Recurrence.FirstLocalOccurrence.AddHours(1) } },
            definition with { TimeZone = definition.TimeZone with { TimeZoneId = "Etc/UTC" } },
            definition with { DaylightSaving = definition.DaylightSaving with { InvalidLocalTime = ScheduleInvalidLocalTimePolicy.Skip } },
            definition with { Misfire = new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.Skip, 0) },
            definition with { Overlap = ScheduleOverlapPolicy.Allow },
            definition with { Enabled = false },
        };

        Assert.All(variants, variant => Assert.NotEqual(expected, DefinitionHash(variant)));
    }

    [Fact]
    public void Definition_hash_supports_every_closed_policy_token()
    {
        foreach (var priority in Enum.GetValues<SchedulePriority>().Where(value => value != SchedulePriority.Unknown))
        {
            AssertCanonical(DefinitionHash(ScheduleContractTestData.Definition() with { Priority = priority }));
        }

        foreach (var recurrence in Enum.GetValues<ScheduleRecurrenceKind>().Where(value => value != ScheduleRecurrenceKind.Unknown))
        {
            var interval = recurrence == ScheduleRecurrenceKind.FixedInterval ? 60 : (long?)null;
            AssertCanonical(DefinitionHash(ScheduleContractTestData.Definition(recurrenceKind: recurrence, fixedIntervalSeconds: interval)));
        }

        foreach (var invalid in Enum.GetValues<ScheduleInvalidLocalTimePolicy>().Where(value => value != ScheduleInvalidLocalTimePolicy.Unknown))
        {
            foreach (var ambiguous in Enum.GetValues<ScheduleAmbiguousLocalTimePolicy>().Where(value => value != ScheduleAmbiguousLocalTimePolicy.Unknown))
            {
                AssertCanonical(DefinitionHash(ScheduleContractTestData.Definition() with { DaylightSaving = new ScheduleDaylightSavingPolicy(invalid, ambiguous) }));
            }
        }

        foreach (var misfire in Enum.GetValues<ScheduleMisfirePolicyKind>().Where(value => value != ScheduleMisfirePolicyKind.Unknown))
        {
            AssertCanonical(DefinitionHash(ScheduleContractTestData.Definition(misfireKind: misfire, catchUpLimit: misfire == ScheduleMisfirePolicyKind.CatchUp ? 1 : 0)));
        }

        foreach (var overlap in Enum.GetValues<ScheduleOverlapPolicy>().Where(value => value != ScheduleOverlapPolicy.Unknown))
        {
            AssertCanonical(DefinitionHash(ScheduleContractTestData.Definition() with { Overlap = overlap }));
        }
    }

    [Fact]
    public void Invalid_definition_or_state_never_produces_a_hash()
    {
        Assert.False(ScheduleContractHash.TryComputeDefinition(null, out var definitionHash, out var definitionValidation));
        Assert.Null(definitionHash);
        Assert.False(definitionValidation.IsValid);

        Assert.False(ScheduleContractHash.TryComputeState(ScheduleContractTestData.State(stateRevision: 0), out var stateHash, out var stateValidation));
        Assert.Null(stateHash);
        Assert.False(stateValidation.IsValid);
    }

    [Fact]
    public void State_hash_is_pinned_and_canonical_across_collection_input_order()
    {
        var first = ScheduleContractTestData.Disposition(1);
        var second = ScheduleContractTestData.Disposition(2);
        var terminalOne = ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(3));
        var terminalTwo = ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(4), ScheduleDeliveryResultKind.Replayed);
        var next = ScheduleContractTestData.OccurrenceAt(5);
        var ordered = ScheduleContractTestData.State(next, dispositions: [first, second], terminal: [terminalOne, terminalTwo]);
        var reversed = ScheduleContractTestData.State(next, dispositions: [second, first], terminal: [terminalTwo, terminalOne]);

        var expected = StateHash(ordered);
        Assert.Equal(expected, StateHash(reversed));
        Assert.Equal("204e11fb40131e8e84754404745d4e7989fc084480a5a9bf624fb4d5bc4640fa", expected);
    }

    [Fact]
    public void Every_state_machine_slice_changes_canonical_state_identity()
    {
        var state = ScheduleContractTestData.State();
        var expected = StateHash(state);
        var claimed = ScheduleContractTestData.Pending();
        var preparedEvidence = ScheduleContractTestData.Prepared();
        var prepared = ScheduleContractTestData.Pending(prepared: preparedEvidence);
        var observed = ScheduleContractTestData.Pending(prepared: preparedEvidence, result: ScheduleContractTestData.Result(preparedEvidence.CanonicalEnvelopeHash));
        var deferred = ScheduleContractTestData.Deferred(state.NextOccurrence!);
        var variants = new[]
        {
            state with { StateRevision = 2 },
            state with { DefinitionRevision = 2 },
            state with { DefinitionHash = new string('1', 64) },
            state with { Enabled = false },
            state with { LastClockObservedAtUtc = null },
            state with { NextOccurrence = null },
            state with { CatchUpEpisode = new ScheduleCatchUpEpisode(1, 2, 1) },
            state with
            {
                DeferredOccurrence = deferred,
                DispositionEvidence =
                [
                    ScheduleContractTestData.Disposition(
                        1,
                        disposition: ScheduleOccurrenceDisposition.OverlapDeferred),
                ],
            },
            state with { PendingDelivery = claimed },
            state with { PendingDelivery = prepared },
            state with { PendingDelivery = observed },
            ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(2), dispositions: [ScheduleContractTestData.Disposition(1)]),
            ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(2), terminal: [ScheduleContractTestData.Terminal(ScheduleContractTestData.OccurrenceAt(1))]),
        };

        Assert.All(variants, variant => Assert.NotEqual(expected, StateHash(variant)));
    }

    [Fact]
    public void Pending_and_terminal_proof_hashes_are_independently_bound_to_state_identity()
    {
        var occurrence = ScheduleContractTestData.Occurrence();
        var prepared = ScheduleContractTestData.Prepared(occurrence);
        var pending = ScheduleContractTestData.Pending(occurrence, prepared);
        var pendingState = ScheduleContractTestData.State(occurrence, pending);
        var pendingHash = StateHash(pendingState);

        Assert.NotEqual(pendingHash, StateHash(pendingState with
        {
            PendingDelivery = pending with { CurrentEvidenceHash = new string('1', 64) },
        }));
        Assert.NotEqual(pendingHash, StateHash(pendingState with
        {
            PendingDelivery = pending with { RecurrenceProofHash = new string('2', 64) },
        }));
        var changedOverlapHash = new string('3', 64);
        var changedOverlapPrepared = ScheduleContractTestData.Prepared(
            occurrence,
            overlapEvidenceHash: changedOverlapHash);
        var changedOverlapPending = ScheduleContractTestData.Pending(
            occurrence,
            changedOverlapPrepared,
            overlapEvidenceHash: changedOverlapHash);
        Assert.NotEqual(
            pendingHash,
            StateHash(pendingState with { PendingDelivery = changedOverlapPending }));

        var terminal = ScheduleContractTestData.Terminal(occurrence);
        var terminalState = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            terminal: [terminal]);
        var terminalHash = StateHash(terminalState);

        Assert.NotEqual(terminalHash, StateHash(terminalState with
        {
            TerminalDeliveryEvidence = [terminal with { CurrentEvidenceHash = new string('1', 64) }],
        }));
        Assert.NotEqual(terminalHash, StateHash(terminalState with
        {
            TerminalDeliveryEvidence = [terminal with { RecurrenceProofHash = new string('2', 64) }],
        }));
        Assert.NotEqual(terminalHash, StateHash(terminalState with
        {
            TerminalDeliveryEvidence = [terminal with { OverlapEvidenceHash = new string('3', 64) }],
        }));

        var disposition = ScheduleContractTestData.Disposition(1);
        var dispositionState = ScheduleContractTestData.State(
            next: ScheduleContractTestData.OccurrenceAt(2),
            dispositions: [disposition]);
        Assert.NotEqual(StateHash(dispositionState), StateHash(dispositionState with
        {
            DispositionEvidence = [disposition with { DecisionEvidenceHash = new string('4', 64) }],
        }));
    }

    [Fact]
    public void State_hash_supports_every_pending_result_disposition_and_optional_branch()
    {
        var preparedEvidence = ScheduleContractTestData.Prepared();
        foreach (var resultKind in Enum.GetValues<ScheduleDeliveryResultKind>().Where(value => value != ScheduleDeliveryResultKind.Unknown))
        {
            var result = ScheduleContractTestData.Result(preparedEvidence.CanonicalEnvelopeHash, resultKind);
            AssertCanonical(StateHash(ScheduleContractTestData.State(pending: ScheduleContractTestData.Pending(prepared: preparedEvidence, result: result))));
        }

        foreach (var disposition in Enum.GetValues<ScheduleOccurrenceDisposition>().Where(value => value != ScheduleOccurrenceDisposition.Unknown))
        {
            var evidence = ScheduleContractTestData.Disposition(1, disposition: disposition);
            AssertCanonical(StateHash(ScheduleContractTestData.State(next: ScheduleContractTestData.OccurrenceAt(2), dispositions: [evidence])));
        }

        var exhaustedPlan = new ScheduleFinalizationPlan(1, null, null, null, []);
        var exhaustedPending = ScheduleContractTestData.Pending(prepared: preparedEvidence, finalizationPlan: exhaustedPlan);
        AssertCanonical(StateHash(ScheduleContractTestData.State(pending: exhaustedPending)));
    }

    [Fact]
    public void Valid_but_oversized_canonical_state_fails_closed_instead_of_silently_truncating()
    {
        var dispositions = Enumerable.Range(1, ScheduleContractLimits.MaxDispositionEvidenceItems)
            .Select(index => ScheduleContractTestData.Disposition(index))
            .ToArray();
        var terminal = Enumerable.Range(1, ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)
            .Select(index => ScheduleContractTestData.Terminal(
                ScheduleContractTestData.OccurrenceAt(index + ScheduleContractLimits.MaxDispositionEvidenceItems)))
            .ToArray();
        var current = ScheduleContractTestData.OccurrenceAt(1000);
        var planEvidence = Enumerable.Range(1001, ScheduleContractLimits.MaxFinalizationEvidenceItems)
            .Select(index => ScheduleContractTestData.Disposition(index))
            .ToArray();
        var plan = new ScheduleFinalizationPlan(1, ScheduleContractTestData.OccurrenceAt(1257), null, null, planEvidence);
        var pending = ScheduleContractTestData.Pending(current, ScheduleContractTestData.Prepared(current), finalizationPlan: plan);
        var state = ScheduleContractTestData.State(current, pending, dispositions, terminal: terminal);
        var validation = ScheduleContractValidator.ValidateState(state);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));

        Assert.False(ScheduleContractHash.TryComputeState(state, out var hash, out validation));
        Assert.Null(hash);
        Assert.Contains(validation.Errors, error => error.Code == "canonical_document_too_large" && error.Path == "$");
    }

    private static string DefinitionHash(ScheduleDefinition definition)
    {
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var hash, out var validation), ScheduleContractTestData.Errors(validation));
        return hash!;
    }

    private static string StateHash(ScheduleState state)
    {
        Assert.True(ScheduleContractHash.TryComputeState(state, out var hash, out var validation), ScheduleContractTestData.Errors(validation));
        return hash!;
    }

    private static void AssertCanonical(string hash)
        => Assert.Matches("^[0-9a-f]{64}$", hash);
}
