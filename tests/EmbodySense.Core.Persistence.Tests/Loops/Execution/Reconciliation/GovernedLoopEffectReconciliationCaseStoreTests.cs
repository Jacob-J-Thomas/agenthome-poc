using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationCaseStoreTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Creates_lists_reads_and_replays_one_case_without_a_second_ledger()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var request = Mutation(scenario.Open, null, null, "open", "operation-open");

        var applied = await store.CompareExchangeAsync(request);

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, applied.Status);
        Assert.Equal(scenario.Open.ContentHash, applied.Case!.ContentHash);
        Assert.Equal(scenario.Attempt.ContentHash, applied.EffectHead!.ContentHash);
        var replay = await store.CompareExchangeAsync(request);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, replay.Status);
        Assert.Equal(applied.Case, replay.Case);
        Assert.Equal(applied.EffectHead, replay.EffectHead);
        var divergent = await store.CompareExchangeAsync(new GovernedLoopEffectReconciliationCaseMutationRequest(
            request.OperationId,
            Hash("divergent-request"),
            request.Purpose,
            request.ExpectedCaseVersion,
            request.ExpectedCaseContentHash,
            request.Binding,
            request.Replacement));
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, divergent.Status);
        Assert.Equal(applied.Case, divergent.Case);

        var read = await store.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(applied.Case!)));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, read.Status);
        Assert.Equal(applied.Case, read.Case);
        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        var summary = Assert.Single(page.Cases);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, page.Status);
        Assert.Equal(applied.Case!.CaseId, summary.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationCaseSummaryStatus.Open, summary.Status);
        Assert.Null(page.NextCursor);

        var files = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath).Select(Path.GetFileName).Where(value => value is not null).ToArray();
        Assert.Contains(files, value => value!.StartsWith("reconciliation-case.", StringComparison.Ordinal) && value.EndsWith(".json", StringComparison.Ordinal));
        Assert.Contains(files, value => value!.StartsWith("reconciliation-case.", StringComparison.Ordinal) && value.EndsWith(".head", StringComparison.Ordinal));
        Assert.Contains(files, value => value!.StartsWith("reconciliation-receipt.", StringComparison.Ordinal) && value.EndsWith(".json", StringComparison.Ordinal));
        Assert.DoesNotContain(files, value => value!.Contains("raw-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_reservation_is_durable_before_callback_and_observation_keeps_original_effect_head()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            1,
            scenario.Open.CaseId,
            scenario.Open.Binding.ContentHash,
            "probe-source",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            scenario.Open.ContractMetadata.ContractId,
            scenario.Open.ContractMetadata.ContractVersion,
            scenario.Open.ContractMetadata.ContentHash,
            Hash('a'),
            scenario.Open.OpenedAtUtc,
            null,
            string.Empty));
        var probeCase = GovernedLoopEffectReconciliationContract.Create(
            scenario.Open.CaseId,
            scenario.Open.CaseVersion,
            scenario.Open.Binding,
            scenario.Open.ContractMetadata,
            [source],
            [],
            [],
            null,
            null,
            null,
            [],
            null,
            scenario.Open.OpenedAtUtc,
            scenario.Open.UpdatedAtUtc);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(probeCase, null, null, "open", "probe-case-open"))).Status);
        var invocation = new GovernedLoopEffectReconciliationProbeInvocationRequest(
            Reference(probeCase),
            probeCase.Binding,
            probeCase.ContractMetadata,
            scenario.Input,
            scenario.Attempt,
            source);
        Assert.Equal(invocation.Binding.ContentHash, invocation.Case.BindingHash);
        Assert.Equal(invocation.Binding.CurrentAttemptHash, invocation.EffectHead!.ContentHash);
        Assert.Equal(invocation.Input.Fingerprint, invocation.EffectHead.InputFingerprint);
        var reservationRequest = new GovernedLoopEffectReconciliationProbeReservationRequest("probe-operation-1", Hash("probe-intent"), invocation);
        var contender = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(scenario.Paths));
        var reservations = await Task.WhenAll(store.ReserveAsync(reservationRequest), contender.ReserveAsync(reservationRequest));
        Assert.Single(reservations, value => value.Status == GovernedLoopEffectReconciliationProbeReservationStatus.Reserved);
        Assert.Single(reservations, value => value.Status == GovernedLoopEffectReconciliationProbeReservationStatus.Replayed);
        var reservation = Assert.Single(reservations, value => value.Status == GovernedLoopEffectReconciliationProbeReservationStatus.Reserved);
        Assert.NotNull(reservation.Reservation);
        Assert.Contains(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path)!.StartsWith("reconciliation-probe-reservation.", StringComparison.Ordinal));
        var reservationJson = await File.ReadAllTextAsync(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-probe-reservation.*.json").Single());
        Assert.Contains("\"inputJson\":\"\"", reservationJson, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Input.CanonicalJson, reservationJson, StringComparison.Ordinal);

        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            1,
            probeCase.CaseId,
            probeCase.Binding.ContentHash,
            "probe-observation-1",
            source.SourceId,
            source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "evidence-1",
            Hash('b'),
            probeCase.OpenedAtUtc.AddSeconds(1),
            probeCase.OpenedAtUtc.AddSeconds(2),
            "No external effect observed.",
            string.Empty));
        var committed = await store.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(
            reservation.Reservation!,
            new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation)));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, committed.Status);
        Assert.NotNull(committed.Case);
        Assert.Equal(scenario.Attempt.ContentHash, committed.EffectHead!.ContentHash);
        Assert.Single(committed.Case!.ObservationHistory);
        var replay = await store.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest("probe-operation-1", Hash("probe-intent"), invocation));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, replay.Status);
        Assert.Equal(committed.Case!.ContentHash, replay.Case!.ContentHash);
        Assert.Equal(committed.EffectHead!.ContentHash, replay.EffectHead!.ContentHash);
        Assert.DoesNotContain(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path)!.Contains("reconciled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Probe_observation_process_loss_recovers_the_single_callback_result_and_replays()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            1,
            scenario.Open.CaseId,
            scenario.Open.Binding.ContentHash,
            "probe-crash-source",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            scenario.Open.ContractMetadata.ContractId,
            scenario.Open.ContractMetadata.ContractVersion,
            scenario.Open.ContractMetadata.ContentHash,
            Hash('a'),
            scenario.Open.OpenedAtUtc,
            null,
            string.Empty));
        var probeCase = GovernedLoopEffectReconciliationContract.Create(
            scenario.Open.CaseId,
            scenario.Open.CaseVersion,
            scenario.Open.Binding,
            scenario.Open.ContractMetadata,
            [source],
            [],
            [],
            null,
            null,
            null,
            [],
            null,
            scenario.Open.OpenedAtUtc,
            scenario.Open.UpdatedAtUtc);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(probeCase, null, null, "open", "probe-crash-open"))).Status);
        var invocation = new GovernedLoopEffectReconciliationProbeInvocationRequest(
            Reference(probeCase),
            probeCase.Binding,
            probeCase.ContractMetadata,
            scenario.Input,
            scenario.Attempt,
            source);
        var reservationRequest = new GovernedLoopEffectReconciliationProbeReservationRequest("probe-crash-operation", Hash("probe-crash-intent"), invocation);
        var reservation = await store.ReserveAsync(reservationRequest);
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation.Status);

        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            1,
            probeCase.CaseId,
            probeCase.Binding.ContentHash,
            "probe-crash-observation",
            source.SourceId,
            source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "crash-evidence",
            Hash('b'),
            probeCase.OpenedAtUtc.AddSeconds(1),
            probeCase.OpenedAtUtc.AddSeconds(2),
            "The callback result was durably captured before process loss.",
            string.Empty));
        var result = new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation);
        var crashingStore = new GovernedLoopEffectReconciliationCaseStore(
            new GovernedLoopEffectAttemptStore(scenario.Paths),
            options: new GovernedLoopEffectReconciliationCaseStoreOptions
            {
                DurableBoundaryObserver = boundary =>
                {
                    if (boundary == GovernedLoopEffectReconciliationPersistenceBoundary.ProbeObservationPublished)
                    {
                        throw new InvalidOperationException("simulated process loss");
                    }
                },
            });

        var interrupted = await crashingStore.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(reservation.Reservation!, result));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable, interrupted.Status);
        Assert.Contains(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path)!.StartsWith("reconciliation-probe-journal.", StringComparison.Ordinal));

        var restarted = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(scenario.Paths));
        Assert.True(await restarted.RecoverAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path)!.StartsWith("reconciliation-probe-journal.", StringComparison.Ordinal));
        var page = await restarted.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        var summary = Assert.Single(page.Cases);
        var recovered = await restarted.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(new GovernedLoopEffectReconciliationCaseReference(summary.CaseId, summary.CaseVersion, summary.ContentHash, summary.BindingHash)));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, recovered.Status);
        Assert.Single(recovered.Case!.ObservationHistory);
        Assert.Equal(scenario.Attempt.ContentHash, (await restarted.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(reservation.Reservation!, result))).EffectHead!.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound, GovernedLoopEffectReconciliationObservationKind.Missing)]
    [InlineData(GovernedLoopEffectReconciliationProbeInvocationStatus.Invalid, GovernedLoopEffectReconciliationObservationKind.UnprovenHash)]
    public async Task Probe_non_ready_invocation_is_normalized_and_committed_without_effect_successor(
        GovernedLoopEffectReconciliationProbeInvocationStatus status,
        GovernedLoopEffectReconciliationObservationKind expectedKind)
    {
        using var workspace = new TestWorkspace();
        var setup = await CreateProbeSetupAsync(workspace, "non-ready-" + status.ToString().ToLowerInvariant());
        var reservation = await setup.Store.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest(
            "probe-" + status.ToString().ToLowerInvariant(),
            Hash(status.ToString()),
            setup.Invocation));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation.Status);

        var committed = await setup.Store.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(
            reservation.Reservation!,
            new GovernedLoopEffectReconciliationProbeInvocationResult(status, null)));

        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, committed.Status);
        Assert.NotNull(committed.Case);
        var observation = Assert.Single(committed.Case!.ObservationHistory);
        Assert.Equal(expectedKind, observation.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.Unknown, observation.ObservedOutcome);
        Assert.Equal(setup.Scenario.Attempt.ContentHash, committed.EffectHead!.ContentHash);
        Assert.DoesNotContain(Directory.EnumerateFiles(setup.Scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path)!.Contains("reconciled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Malformed_probe_reservation_observation_and_journal_fail_closed_in_readiness()
    {
        using var reservationWorkspace = new TestWorkspace();
        var reservationSetup = await CreateProbeSetupAsync(reservationWorkspace, "malformed-reservation");
        var reservation = await reservationSetup.Store.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest(
            "probe-malformed-reservation",
            Hash("malformed-reservation"),
            reservationSetup.Invocation));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation.Status);
        var reservationPath = Directory.EnumerateFiles(reservationSetup.Scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-probe-reservation.*.json").Single();
        await File.WriteAllTextAsync(reservationPath, "{}");
        Assert.False(await reservationSetup.Store.ProbeStorageAvailabilityAsync());

        using var observationWorkspace = new TestWorkspace();
        var observationSetup = await CreateProbeSetupAsync(observationWorkspace, "malformed-observation");
        var observationReservation = await observationSetup.Store.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest(
            "probe-malformed-observation",
            Hash("malformed-observation"),
            observationSetup.Invocation));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, observationReservation.Status);
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            1,
            observationSetup.ProbeCase.CaseId,
            observationSetup.ProbeCase.Binding.ContentHash,
            "malformed-observation-result",
            observationSetup.Source.SourceId,
            observationSetup.Source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            observationSetup.Source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "malformed-evidence",
            Hash('b'),
            observationSetup.ProbeCase.OpenedAtUtc.AddSeconds(1),
            observationSetup.ProbeCase.OpenedAtUtc.AddSeconds(2),
            "No external effect observed.",
            string.Empty));
        var observationCommit = await observationSetup.Store.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(
            observationReservation.Reservation!,
            new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation)));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, observationCommit.Status);
        var observationPath = Directory.EnumerateFiles(observationSetup.Scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-probe-observation.*.json").Single();
        await File.WriteAllTextAsync(observationPath, "{}");
        Assert.False(await observationSetup.Store.ProbeStorageAvailabilityAsync());

        using var journalWorkspace = new TestWorkspace();
        var journalSetup = await CreateProbeSetupAsync(journalWorkspace, "malformed-journal");
        var journalReservation = await journalSetup.Store.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest(
            "probe-malformed-journal",
            Hash("malformed-journal"),
            journalSetup.Invocation));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, journalReservation.Status);
        var journalObservation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            1,
            journalSetup.ProbeCase.CaseId,
            journalSetup.ProbeCase.Binding.ContentHash,
            "malformed-journal-result",
            journalSetup.Source.SourceId,
            journalSetup.Source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            journalSetup.Source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "malformed-journal-evidence",
            Hash('c'),
            journalSetup.ProbeCase.OpenedAtUtc.AddSeconds(1),
            journalSetup.ProbeCase.OpenedAtUtc.AddSeconds(2),
            "No external effect observed.",
            string.Empty));
        var crashingStore = new GovernedLoopEffectReconciliationCaseStore(
            new GovernedLoopEffectAttemptStore(journalSetup.Scenario.Paths),
            options: new GovernedLoopEffectReconciliationCaseStoreOptions
            {
                DurableBoundaryObserver = boundary =>
                {
                    if (boundary == GovernedLoopEffectReconciliationPersistenceBoundary.ProbeObservationPublished)
                    {
                        throw new InvalidOperationException("simulated process loss");
                    }
                },
            });
        var interrupted = await crashingStore.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(
            journalReservation.Reservation!,
            new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, journalObservation)));
        Assert.Equal(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable, interrupted.Status);
        var journalPath = Directory.EnumerateFiles(journalSetup.Scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-probe-journal.*.json").Single();
        await File.WriteAllTextAsync(journalPath, "{}");
        Assert.False(await journalSetup.Store.ProbeStorageAvailabilityAsync());
    }

    [Fact]
    public async Task Divergent_same_operation_conflicts_and_stale_case_update_preserves_the_winner()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var openRequest = Mutation(scenario.Open, null, null, "open", "operation-open");
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(openRequest)).Status);
        var assessed = Assessed(scenario.Open, "assessment-1", 'a');
        var update = Mutation(assessed, scenario.Open.CaseVersion, scenario.Open.ContentHash, "assess", "operation-assess");

        var updated = await store.CompareExchangeAsync(update);

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, updated.Status);
        var stale = await store.CompareExchangeAsync(Mutation(assessed, scenario.Open.CaseVersion, scenario.Open.ContentHash, "stale", "operation-stale"));
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, stale.Status);
        Assert.Equal(assessed.ContentHash, stale.Case!.ContentHash);
        Assert.Equal(scenario.Attempt.ContentHash, stale.EffectHead!.ContentHash);
        var oldRead = await store.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(scenario.Open)));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, oldRead.Status);
        var currentRead = await store.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(assessed)));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, currentRead.Status);
    }

    [Fact]
    public async Task Earlier_operation_receipt_replays_its_exact_immutable_result_after_later_case_versions()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var openRequest = Mutation(scenario.Open, null, null, "open", "operation-open");
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(openRequest)).Status);
        var assessed = Assessed(scenario.Open, "assessment-later", 'a');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(assessed, 1, scenario.Open.ContentHash, "assess", "operation-later"))).Status);

        var replay = await store.CompareExchangeAsync(openRequest);

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, replay.Status);
        Assert.Equal(scenario.Open.ContentHash, replay.Case!.ContentHash);
        Assert.Equal(scenario.Attempt.ContentHash, replay.EffectHead!.ContentHash);
    }

    [Fact]
    public async Task Concurrent_case_updates_have_one_compare_exchange_winner()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var first = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(scenario.Paths));
        var second = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(scenario.Paths));
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await first.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var left = Assessed(scenario.Open, "assessment-left", 'b');
        var right = Assessed(scenario.Open, "assessment-right", 'c');

        var results = await Task.WhenAll(
            first.CompareExchangeAsync(Mutation(left, scenario.Open.CaseVersion, scenario.Open.ContentHash, "assess", "operation-left")),
            second.CompareExchangeAsync(Mutation(right, scenario.Open.CaseVersion, scenario.Open.ContentHash, "assess", "operation-right")));

        Assert.Single(results, result => result.Status == GovernedLoopEffectReconciliationCaseMutationStatus.Applied);
        var loser = Assert.Single(results, result => result.Status == GovernedLoopEffectReconciliationCaseMutationStatus.Conflict);
        Assert.NotNull(loser.Case);
        Assert.NotNull(loser.EffectHead);
        Assert.NotEqual(left.ContentHash, right.ContentHash);
        var final = await first.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, final.Status);
        Assert.Single(final.Cases);
        Assert.Equal(GovernedLoopEffectReconciliationCaseSummaryStatus.Assessed, final.Cases[0].Status);
    }

    [Fact]
    public async Task List_uses_canonical_cursor_and_rejects_tampered_or_unknown_continuations()
    {
        using var workspace = new TestWorkspace();
        var first = await CreateScenarioAsync(workspace, "1");
        var second = await CreateScenarioAsync(workspace, "2");
        var store = new GovernedLoopEffectReconciliationCaseStore(first.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(first.Open, null, null, "open", "operation-open-1"))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(second.Open, null, null, "open", "operation-open-2"))).Status);

        var firstPage = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(1));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, firstPage.Status);
        Assert.Single(firstPage.Cases);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(1, firstPage.NextCursor));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, secondPage.Status);
        Assert.Equal("case-2", Assert.Single(secondPage.Cases).CaseId);
        Assert.Null(secondPage.NextCursor);

        var tampered = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(1, firstPage.NextCursor + "="));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, tampered.Status);
        var unknownCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("v1\ncase-unknown")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unknown = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(1, unknownCursor));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, unknown.Status);
    }

    [Fact]
    public async Task Accepted_resolution_commits_one_typed_effect_successor_and_exact_replay()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = ProvedAssessed(scenario.Open);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(assessed, 1, scenario.Open.ContentHash, "assess", "operation-assess"))).Status);
        var disposed = Disposed(assessed, 'e');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(disposed, 2, assessed.ContentHash, "dispose", "operation-dispose"))).Status);
        var resolved = Resolved(disposed, scenario.Attempt, 'f');
        var resolvedRequest = Mutation(resolved.Case, 3, disposed.ContentHash, "resolve", "operation-resolve", resolved.Successor);

        var applied = await store.CompareExchangeAsync(resolvedRequest);

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, applied.Status);
        Assert.Equal(resolved.Case.ContentHash, applied.Case!.ContentHash);
        Assert.Equal(resolved.Successor.ContentHash, applied.EffectHead!.ContentHash);
        var effect = await scenario.EffectStore.ReadAsync(scenario.WorkspaceId, scenario.Attempt.Payload.OperationId, scenario.Attempt.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, effect.Status);
        Assert.Equal(GovernedLoopEffectPhase.Reconciled, effect.Attempt!.Payload.Phase);
        var resolution = await store.ReadAsync(new GovernedLoopEffectReconciliationResolutionReadRequest(Reference(resolved.Case), resolved.Case.Binding));
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Found, resolution.Status);
        var replay = await store.CompareExchangeAsync(resolvedRequest);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, replay.Status);
        Assert.Equal(applied.Case!.ContentHash, replay.Case!.ContentHash);
        Assert.Equal(applied.EffectHead!.ContentHash, replay.EffectHead!.ContentHash);
        Assert.Equal(5, Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "*.json").Count(value => !Path.GetFileName(value).StartsWith("reconciliation-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Malformed_head_and_missing_root_fail_closed_without_repairing_read_only_state()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var head = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.head").Single();
        await File.WriteAllTextAsync(head, "not-a-hash");

        var listed = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));

        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, listed.Status);
        Assert.Empty(listed.Cases);
        Assert.Null(listed.NextCursor);

        using var emptyWorkspace = new TestWorkspace();
        var emptyPaths = new WorkspacePaths(emptyWorkspace.RootPath);
        var emptyStore = new GovernedLoopEffectReconciliationCaseStore(emptyPaths);
        var emptyPage = await emptyStore.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, emptyPage.Status);
        Assert.Empty(emptyPage.Cases);
    }

    [Fact]
    public async Task Corrupt_receipt_is_rejected_by_list_and_readiness_probe()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var request = Mutation(scenario.Open, null, null, "open", "operation-open");
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(request)).Status);
        var receipt = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-receipt.*.json").Single();
        await File.WriteAllTextAsync(receipt, "{}");

        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));

        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, page.Status);
        Assert.False(await store.ProbeStorageAvailabilityAsync());
    }

    [Fact]
    public async Task Corrupt_case_version_is_rejected_by_the_canonical_inventory()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var caseVersion = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.json").Single();
        await File.WriteAllTextAsync(caseVersion, "{}");

        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));

        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, page.Status);
        Assert.Empty(page.Cases);
    }

    [Fact]
    public async Task Forged_reconciled_head_without_the_exact_canonical_resolution_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = ProvedAssessed(scenario.Open);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(assessed, 1, scenario.Open.ContentHash, "assess", "operation-assess"))).Status);
        var disposed = Disposed(assessed, 'e');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(disposed, 2, assessed.ContentHash, "dispose", "operation-dispose"))).Status);
        var resolved = Resolved(disposed, scenario.Attempt, 'f');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(resolved.Case, 3, disposed.ContentHash, "resolve", "operation-resolve"))).Status);

        var forged = ForgedSuccessor(scenario.Attempt, resolved.Case);
        var headPath = EffectHeadPath(scenario.Paths);
        var storageKey = Path.GetFileName(headPath)[..^5];
        await File.WriteAllBytesAsync(Path.Combine(scenario.Paths.GovernedLoopEffectAttemptsPath, $"{storageKey}.{forged.ContentHash}.json"), GovernedLoopEffectAttemptRecordCodec.Encode(forged));
        await File.WriteAllTextAsync(headPath, forged.ContentHash);

        var read = await scenario.EffectStore.ReadAsync(scenario.WorkspaceId, scenario.Attempt.Payload.OperationId, scenario.Attempt.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, read.Status);
        Assert.False(await scenario.EffectStore.ProbeStorageAvailabilityAsync());
        Assert.False(await store.ProbeStorageAvailabilityAsync());
    }

    [Fact]
    public async Task Reconciled_head_without_a_current_case_head_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = ProvedAssessed(scenario.Open);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(assessed, 1, scenario.Open.ContentHash, "assess", "operation-assess"))).Status);
        var disposed = Disposed(assessed, 'e');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(disposed, 2, assessed.ContentHash, "dispose", "operation-dispose"))).Status);
        var resolved = Resolved(disposed, scenario.Attempt, 'f');
        var request = Mutation(resolved.Case, 3, disposed.ContentHash, "resolve", "operation-resolve", resolved.Successor);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(request)).Status);
        var caseHead = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.head").Single();
        File.Delete(caseHead);

        var read = await scenario.EffectStore.ReadAsync(scenario.WorkspaceId, scenario.Attempt.Payload.OperationId, scenario.Attempt.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, read.Status);
        Assert.False(await scenario.EffectStore.ProbeStorageAvailabilityAsync());
        Assert.False(await store.ProbeStorageAvailabilityAsync());
    }

    [Fact]
    public async Task Public_effect_probe_propagates_cancellation_before_reconciliation_proof_inventory()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.EffectStore.ProbeStorageAvailabilityAsync(cancellation.Token));
    }

    [Fact]
    public async Task Readiness_fails_closed_when_retained_reconciliation_artifacts_exceed_store_quota()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var initialStore = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var openRequest = Mutation(scenario.Open, null, null, "open", "operation-quota");
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await initialStore.CompareExchangeAsync(openRequest)).Status);

        var artifactPaths = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath).ToArray();
        var retainedBytes = artifactPaths.Sum(path => new FileInfo(path).Length);
        var maximumRecordBytes = artifactPaths.Max(path => new FileInfo(path).Length);
        Assert.True(retainedBytes > maximumRecordBytes);
        var constrainedOptions = new GovernedLoopEffectAttemptStoreOptions
        {
            MaxRecordUtf8Bytes = checked((int)maximumRecordBytes),
            MaxStoreUtf8Bytes = retainedBytes - 1,
        };
        var constrainedEffectStore = new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspace.RootPath), constrainedOptions);
        var constrainedCaseStore = new GovernedLoopEffectReconciliationCaseStore(constrainedEffectStore);

        var read = await constrainedEffectStore.ReadAsync(scenario.WorkspaceId, scenario.Attempt.Payload.OperationId, scenario.Attempt.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, read.Status);
        Assert.False(await constrainedEffectStore.ProbeStorageAvailabilityAsync());
        Assert.False(await constrainedCaseStore.ProbeStorageAvailabilityAsync());
    }

    [Fact]
    public async Task Missing_expected_case_cannot_return_payload_bearing_conflict()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        var invalid = await store.CompareExchangeAsync(Mutation(Assessed(scenario.Open, "assessment-missing", 'f'), 1, scenario.Open.ContentHash, "invalid", "operation-invalid"));

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid, invalid.Status);
        Assert.Null(invalid.Case);
        Assert.Null(invalid.EffectHead);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.JournalPublished)]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.CasePublished)]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.ReceiptPublished)]
    public async Task External_process_loss_at_case_publication_boundaries_recovers_and_replays_exactly(
        GovernedLoopEffectReconciliationPersistenceBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = Assessed(scenario.Open, "assessment-crash", 'a');
        var request = Mutation(assessed, scenario.Open.CaseVersion, scenario.Open.ContentHash, "assess", "operation-crash");
        var result = await RunExternalCrashAsync(workspace.RootPath, boundary, request);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(result.OutputPath));
        Assert.Contains(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path).StartsWith("reconciliation-journal.", StringComparison.Ordinal));
        Assert.True(await store.RecoverAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path).StartsWith("reconciliation-journal.", StringComparison.Ordinal));

        var replay = await store.CompareExchangeAsync(request);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, replay.Status);
        Assert.Equal(assessed.ContentHash, replay.Case!.ContentHash);
        Assert.Equal(scenario.Attempt.ContentHash, replay.EffectHead!.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.EffectPublished)]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.EffectVersionPublished)]
    public async Task External_process_loss_at_effect_successor_boundaries_recovers_without_duplicate_transition(
        GovernedLoopEffectReconciliationPersistenceBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = ProvedAssessed(scenario.Open);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(assessed, 1, scenario.Open.ContentHash, "assess", "operation-assess"))).Status);
        var disposed = Disposed(assessed, 'e');
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(disposed, 2, assessed.ContentHash, "dispose", "operation-dispose"))).Status);
        var resolved = Resolved(disposed, scenario.Attempt, 'f');
        var request = Mutation(resolved.Case, 3, disposed.ContentHash, "resolve", "operation-crash-successor", resolved.Successor);
        var result = await RunExternalCrashAsync(workspace.RootPath, boundary, request);

        Assert.NotEqual(0, result.ExitCode);
        if (boundary == GovernedLoopEffectReconciliationPersistenceBoundary.EffectVersionPublished)
        {
            var headPath = EffectHeadPath(scenario.Paths);
            Assert.Equal(scenario.Attempt.ContentHash, await File.ReadAllTextAsync(headPath));
            var effectVersion = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "*.json")
                .Single(path => Path.GetFileName(path).Contains(resolved.Successor.ContentHash, StringComparison.Ordinal));
            Assert.True(File.Exists(effectVersion));
        }
        Assert.True(await store.RecoverAsync());
        var replay = await store.CompareExchangeAsync(request);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, replay.Status);
        Assert.Equal(resolved.Case.ContentHash, replay.Case!.ContentHash);
        Assert.Equal(resolved.Successor.ContentHash, replay.EffectHead!.ContentHash);
        var current = await scenario.EffectStore.ReadAsync(scenario.WorkspaceId, scenario.Attempt.Payload.OperationId, scenario.Attempt.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, current.Status);
        Assert.Equal(resolved.Successor.ContentHash, current.Attempt!.ContentHash);
        Assert.DoesNotContain(Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath), path => Path.GetFileName(path).StartsWith("reconciliation-journal.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_pending_journal_is_corrupt_for_read_and_not_silently_replayed()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, null, null, "open", "operation-open"))).Status);
        var assessed = Assessed(scenario.Open, "assessment-pending", 'a');
        var request = Mutation(assessed, scenario.Open.CaseVersion, scenario.Open.ContentHash, "assess", "operation-pending");
        var crash = await RunExternalCrashAsync(workspace.RootPath, GovernedLoopEffectReconciliationPersistenceBoundary.JournalPublished, request);
        Assert.NotEqual(0, crash.ExitCode);
        var journal = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-journal.*.json").Single();
        await File.WriteAllTextAsync(journal, "{}");

        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));

        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, page.Status);
        Assert.False(await store.ProbeStorageAvailabilityAsync());
    }

    private static async Task<ExternalCrashResult> RunExternalCrashAsync(
        string workspace,
        GovernedLoopEffectReconciliationPersistenceBoundary boundary,
        GovernedLoopEffectReconciliationCaseMutationRequest request)
    {
        var root = new DirectoryInfo(workspace);
        var gate = Path.Combine(root.FullName, "reconciliation-process-gate");
        var ready = Path.Combine(root.FullName, "reconciliation-process-ready");
        var output = Path.Combine(root.FullName, "reconciliation-process-output");
        var replacement = Convert.ToBase64String(GovernedLoopEffectReconciliationRecordCodec.Encode(request.Replacement));
        var successor = request.ReconciledEffectSuccessor is null
            ? string.Empty
            : Convert.ToBase64String(GovernedLoopEffectAttemptRecordCodec.Encode(request.ReconciledEffectSuccessor));
        using var process = Verification.CancellationHostProcess.StartOwned(
            "governed-loop-effect-reconciliation",
            workspace,
            gate,
            ready,
            output,
            request.OperationId,
            request.RequestHash,
            request.Purpose,
            request.ExpectedCaseVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            request.ExpectedCaseContentHash ?? string.Empty,
            replacement,
            successor,
            boundary.ToString());
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new(process.ExitCode, output);
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(30), $"The reconciliation process did not create `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task<Scenario> CreateScenarioAsync(TestWorkspace workspace, string suffix = "1")
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var effectStore = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare(suffix);
        var created = await effectStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('6'), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, _now.AddSeconds(2));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, _now.AddSeconds(3));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(prepared.ContentHash, authorized, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(authorized.ContentHash, crossed, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(crossed.ContentHash, attempt, created.Lease!)).Status);
        created.Lease!.Dispose();
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(workspaceId, 1, 1, attempt);
        var metadata = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(1, "contract-1", 1, attempt.Capability, attempt.Implementation, attempt.ActuatorOperationId, attempt.OperationDescriptorHash, "probe-1", 1, Hash('7'), string.Empty));
        var open = GovernedLoopEffectReconciliationContract.Open("case-" + suffix, binding, metadata, [], [], _now.AddSeconds(4));
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(open, attempt).IsValid);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{}", out var input, out _));
        return new Scenario(paths, effectStore, workspaceId, attempt, open, input!);
    }

    private static async Task<(Scenario Scenario, GovernedLoopEffectReconciliationCaseStore Store, GovernedLoopEffectReconciliationCase ProbeCase, GovernedLoopEffectReconciliationEvidenceSource Source, GovernedLoopEffectReconciliationProbeInvocationRequest Invocation)> CreateProbeSetupAsync(TestWorkspace workspace, string suffix)
    {
        var scenario = await CreateScenarioAsync(workspace, suffix);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            1,
            scenario.Open.CaseId,
            scenario.Open.Binding.ContentHash,
            "probe-source-" + suffix,
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            scenario.Open.ContractMetadata.ContractId,
            scenario.Open.ContractMetadata.ContractVersion,
            scenario.Open.ContractMetadata.ContentHash,
            Hash('a'),
            scenario.Open.OpenedAtUtc,
            null,
            string.Empty));
        var probeCase = GovernedLoopEffectReconciliationContract.Create(
            scenario.Open.CaseId,
            scenario.Open.CaseVersion,
            scenario.Open.Binding,
            scenario.Open.ContractMetadata,
            [source],
            [],
            [],
            null,
            null,
            null,
            [],
            null,
            scenario.Open.OpenedAtUtc,
            scenario.Open.UpdatedAtUtc);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(probeCase, null, null, "open", "probe-case-open-" + suffix))).Status);
        var invocation = new GovernedLoopEffectReconciliationProbeInvocationRequest(
            Reference(probeCase),
            probeCase.Binding,
            probeCase.ContractMetadata,
            scenario.Input,
            scenario.Attempt,
            source);
        return (scenario, store, probeCase, source, invocation);
    }

    private static GovernedLoopEffectAttempt Prepare(string suffix = "1")
    {
        Assert.True(CapabilityId.TryParse("org.example/effects/probe", out var capabilityId, out var capabilityError), capabilityError?.Message);
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out var versionError), versionError?.Message);
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('1'), out var descriptorHash, out var descriptorError), descriptorError?.Message);
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out var providerError), providerError?.Message);
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('2'));
        var execution = GovernedLoopExecutionBinding.Create(1, "run-1", revision, 1);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{}", out var input, out _));
        return GovernedLoopEffectAttemptContract.Prepare(execution, "action-" + suffix, 1, new CapabilityDescriptorIdentity(capabilityId!, version!, descriptorHash!), new CapabilityImplementationIdentity(provider!, "effects/probe"), "probe/observe", Hash('3'), "effect-" + suffix, "effect-operation-" + suffix, 1, input!.Fingerprint, Hash('5'), Hash('8'), Hash('9'), "before-alpha", _now);
    }

    private static GovernedLoopEffectReconciliationCase Assessed(GovernedLoopEffectReconciliationCase open, string assessmentId, char authorityHash)
    {
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(1, open.CaseId, open.Binding.ContentHash, assessmentId, GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, [], Hash(authorityHash), open.UpdatedAtUtc.AddSeconds(1), "Awaiting conclusive evidence.", string.Empty));
        return GovernedLoopEffectReconciliationContract.Create(open.CaseId, open.CaseVersion + 1, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, [assessment], assessment.ContentHash, null, null, open.CaseReceiptHashes, open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
    }

    private static GovernedLoopEffectReconciliationCase ProvedAssessed(GovernedLoopEffectReconciliationCase open)
    {
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(1, open.CaseId, open.Binding.ContentHash, "source-1", GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative, GovernedLoopEffectReconciliationReliabilityPosture.Authoritative, open.ContractMetadata.ContractId, open.ContractMetadata.ContractVersion, open.ContractMetadata.ContentHash, Hash('1'), open.OpenedAtUtc, null, string.Empty));
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(1, open.CaseId, open.Binding.ContentHash, "observation-1", source.SourceId, source.ContentHash, GovernedLoopEffectReconciliationObservationKind.Evidence, source.ReliabilityPosture, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, "evidence-1", Hash('2'), open.OpenedAtUtc.AddSeconds(1), open.OpenedAtUtc.AddSeconds(2), "No matching external effect exists.", string.Empty));
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(1, open.CaseId, open.Binding.ContentHash, "assessment-accepted", GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, [observation.ContentHash], Hash('3'), open.OpenedAtUtc.AddSeconds(3), "Authoritative evidence proves absence.", string.Empty));
        return GovernedLoopEffectReconciliationContract.Create(open.CaseId, open.CaseVersion + 1, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, null, null, open.CaseReceiptHashes, open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
    }

    private static GovernedLoopEffectReconciliationCase Disposed(GovernedLoopEffectReconciliationCase assessed, char authorityHash)
    {
        var disposition = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(1, assessed.CaseId, assessed.Binding.ContentHash, "disposition-1", GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, assessed.CurrentAssessmentHash!, Hash(authorityHash), assessed.UpdatedAtUtc.AddSeconds(1), "Accept exact absence proof.", string.Empty));
        return GovernedLoopEffectReconciliationContract.Create(assessed.CaseId, assessed.CaseVersion + 1, assessed.Binding, assessed.ContractMetadata, assessed.EvidenceSources, assessed.ObservationHistory, assessed.AssessmentHistory, assessed.CurrentAssessmentHash, disposition, null, assessed.CaseReceiptHashes, assessed.ContentHash, assessed.OpenedAtUtc, disposition.DisposedAtUtc);
    }

    private static (GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectAttempt Successor) Resolved(GovernedLoopEffectReconciliationCase disposed, GovernedLoopEffectAttempt current, char authorityHash)
    {
        var assessment = disposed.AssessmentHistory[^1];
        var disposition = disposed.Disposition!;
        var resolution = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(1, disposed.CaseId, disposed.Binding.ContentHash, "resolution-1", assessment.ContentHash, disposition.ContentHash, GovernedLoopEffectOutcome.NotApplied, null, null, Hash(authorityHash), disposed.UpdatedAtUtc.AddSeconds(1), "Resolve as not applied.", string.Empty));
        var resolved = GovernedLoopEffectReconciliationContract.Create(disposed.CaseId, disposed.CaseVersion + 1, disposed.Binding, disposed.ContractMetadata, disposed.EvidenceSources, disposed.ObservationHistory, disposed.AssessmentHistory, disposed.CurrentAssessmentHash, disposition, resolution, disposed.CaseReceiptHashes, disposed.ContentHash, disposed.OpenedAtUtc, resolution.ResolvedAtUtc);
        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, resolved);
        return (resolved, successor);
    }

    private static GovernedLoopEffectReconciliationCaseMutationRequest Mutation(GovernedLoopEffectReconciliationCase replacement, long? expectedVersion, string? expectedHash, string purpose, string operationId, GovernedLoopEffectAttempt? successor = null)
        => new(operationId, Hash(operationId), purpose, expectedVersion, expectedHash, replacement.Binding, replacement, successor);

    private static GovernedLoopEffectAttempt ForgedSuccessor(
        GovernedLoopEffectAttempt current,
        GovernedLoopEffectReconciliationCase resolved)
    {
        var resolution = resolved.Resolution!;
        var payload = GovernedLoopEffectPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.EffectId,
            current.Payload.OperationId,
            current.Payload.EffectGeneration,
            current.Payload.Origin,
            current.Payload.OriginNodeId,
            current.Payload.IntentHash,
            GovernedLoopEffectPhase.Reconciled,
            resolution.Outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            resolution.OutcomeEvidenceId,
            "forged-resolution",
            resolution.ResolvedAtUtc);
        var forged = current with
        {
            AfterEvidenceId = current.AfterEvidenceId ?? resolution.OutcomeEvidenceId,
            Payload = payload,
            PreviousContentHash = current.ContentHash,
            ContentHash = string.Empty,
        };
        return forged with { ContentHash = GovernedLoopEffectAttemptContract.Compute(forged) };
    }

    private static string EffectHeadPath(WorkspacePaths paths)
        => Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head")
            .Single(path => !Path.GetFileName(path).StartsWith("reconciliation-", StringComparison.Ordinal));

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static string Hash(char value) => new(value, 64);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ExternalCrashResult(int ExitCode, string OutputPath);

    private sealed record Scenario(WorkspacePaths Paths, GovernedLoopEffectAttemptStore EffectStore, string WorkspaceId, GovernedLoopEffectAttempt Attempt, GovernedLoopEffectReconciliationCase Open, GovernedActuatorInputEvidence Input);
}
