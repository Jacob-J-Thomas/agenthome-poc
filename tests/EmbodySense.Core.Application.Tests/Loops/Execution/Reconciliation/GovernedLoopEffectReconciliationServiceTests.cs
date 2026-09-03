using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationServiceTests
{
    [Fact]
    public async Task Open_assess_and_quarantine_preserve_the_exact_case_and_never_create_a_successor()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);

        var opened = await service.OpenAsync(new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, opened.Status);
        Assert.NotNull(opened.Case);
        Assert.Equal(opened.Case!.Binding.Execution.RunId, opened.EffectHead!.Binding.RunId);

        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(opened.Case)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, assessed.Case!.AssessmentHistory.Single().Kind);

        var quarantined = await service.DisposeAsync(new GovernedLoopEffectReconciliationDispositionRequest("dispose-1", Reference(assessed.Case), GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, quarantined.Status);
        Assert.Equal(GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, quarantined.Case!.Disposition!.Kind);
        Assert.Null(quarantined.Case.Resolution);
        Assert.Equal(attempt.ContentHash, store.CurrentEffect!.ContentHash);

        var resolution = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(quarantined.Case)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, resolution.Status);
        Assert.Null(resolution.Case);
        Assert.Equal(attempt.ContentHash, store.CurrentEffect!.ContentHash);
        Assert.Equal(4, authority.Calls);
    }

    [Fact]
    public async Task Open_replays_the_exact_operation_after_a_fresh_timestamp_without_reopening_the_case()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(
            store,
            authority,
            input,
            new GovernedLoopEffectReconciliationServiceTimeProvider(open.UpdatedAtUtc, open.UpdatedAtUtc.AddTicks(1)));
        var request = new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes);

        var applied = await service.OpenAsync(request);
        var replayed = await service.OpenAsync(request);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, applied.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(applied.Case!.ContentHash, replayed.Case!.ContentHash);
        Assert.Equal(applied.Case.UpdatedAtUtc, open.UpdatedAtUtc);
        Assert.Equal(2, authority.Calls);
        Assert.Equal(2, input.ReadCalls);
        Assert.Equal(2, store.MutationCalls);
        Assert.Equal(1, store.AppliedMutationCalls);
    }

    [Fact]
    public async Task Assessment_history_identifiers_remain_canonically_sorted_at_single_and_double_digit_boundaries()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(open);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var service = new GovernedLoopEffectReconciliationService(
            store,
            new GovernedLoopEffectReconciliationServiceAuthority(),
            input,
            new GovernedLoopEffectReconciliationServiceTimeProvider(open.UpdatedAtUtc));
        var current = open;

        for (var index = 1; index <= 32; index++)
        {
            var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest($"assess-{index}", Reference(current)));
            Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
            current = assessed.Case!;
        }

        var identifiers = current.AssessmentHistory.Select(assessment => assessment.AssessmentId).ToArray();
        Assert.Equal("assessment-09", identifiers[8]);
        Assert.Equal("assessment-10", identifiers[9]);
        Assert.Equal("assessment-31", identifiers[30]);
        Assert.Equal("assessment-32", identifiers[31]);
        Assert.Equal(identifiers.Order(StringComparer.Ordinal), identifiers);
    }

    [Fact]
    public async Task Same_operation_replays_exactly_and_divergent_operation_hash_is_a_conflict()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);
        var first = await service.OpenAsync(new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes));

        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(first.Case!)));
        var replayed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(first.Case!)));
        var conflict = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(first.Case!), "different safe detail"));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(assessed.Case!.ContentHash, replayed.Case!.ContentHash);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, conflict.Status);
        Assert.Equal(store.CurrentCase!.ContentHash, conflict.Case!.ContentHash);
    }

    [Fact]
    public async Task Proved_not_applied_acceptance_publishes_one_typed_successor_and_replay_is_exact()
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationServiceCaseFactory.OpenCaseWithNotAppliedEvidence();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);
        store.SeedCase(value);

        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(value)));
        var disposed = await service.DisposeAsync(new GovernedLoopEffectReconciliationDispositionRequest("dispose-1", Reference(assessed.Case!), GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied));
        var resolved = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(disposed.Case!)));
        var replayed = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(disposed.Case!)));

        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, assessed.Case!.AssessmentHistory.Single().Kind);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, disposed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, resolved.Status);
        Assert.Equal(GovernedLoopEffectOutcome.NotApplied, resolved.Case!.Resolution!.Outcome);
        Assert.Equal(GovernedLoopEffectPhase.Reconciled, resolved.EffectHead!.Payload.Phase);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(resolved.Case.ContentHash, replayed.Case!.ContentHash);

        input.SetStatus(GovernedLoopEffectReconciliationInputReadStatus.NotFound);
        var currentReference = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-again", Reference(resolved.Case!)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, currentReference.Status);
        Assert.NotNull(currentReference.Case);
        Assert.Null(currentReference.EffectHead);
    }

    [Fact]
    public async Task Denied_authority_and_unavailable_input_fail_closed_without_case_mutation()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority { Status = GovernedLoopEffectReconciliationAuthorizationStatus.Denied };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);

        var denied = await service.OpenAsync(new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Denied, denied.Status);
        Assert.Null(store.CurrentCase);

        authority.Status = GovernedLoopEffectReconciliationAuthorizationStatus.Ready;
        input.SetStatus(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        var unavailable = await service.OpenAsync(new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, unavailable.Status);
        Assert.Null(store.CurrentCase);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_mutation_and_original_actuator_is_unreachable()
    {
        var (open, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, open);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.OpenAsync(new GovernedLoopEffectReconciliationOpenRequest("open-1", open.CaseId, open.Binding, open.ContractMetadata, open.EvidenceSources, open.CaseReceiptHashes), cancellation.Token));

        Assert.Null(store.CurrentCase);
        Assert.Null(store.CurrentEffect!.Payload.OutcomeEvidenceId);
    }

    [Fact]
    public async Task Probe_reserves_the_exact_retained_context_invokes_once_and_replays_without_a_successor()
    {
        var (value, attempt, inputValue, source) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        registry.ProbeResultFactory = invocation =>
        {
            var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                "probe-observation-1",
                source.SourceId,
                source.ContentHash,
                GovernedLoopEffectReconciliationObservationKind.Evidence,
                source.ReliabilityPosture,
                GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
                "probe-evidence-1",
                GovernedLoopEffectAttemptTestFixture.Hash('c'),
                attempt.Payload.UpdatedAtUtc.AddSeconds(1),
                attempt.Payload.UpdatedAtUtc.AddSeconds(2),
                "No matching external effect exists.",
                string.Empty));
            return new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation);
        };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input, registry, store);
        var request = new GovernedLoopEffectReconciliationProbeRequest("probe-operation-1", Reference(value));

        var applied = await service.ProbeAsync(request);
        var replayed = await service.ProbeAsync(request);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, applied.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.NotNull(applied.Case);
        Assert.Single(applied.Case!.ObservationHistory);
        Assert.Equal(attempt.ContentHash, applied.EffectHead!.ContentHash);
        Assert.Equal(attempt.TargetFingerprint, registry.LastInvocation!.Target.TargetFingerprint);
        Assert.Equal(attempt.PreconditionEvidenceHash, registry.LastInvocation.Target.PreconditionEvidenceHash);
        Assert.Equal(attempt.BeforeEvidenceId, registry.LastInvocation.Target.BeforeEvidenceId);
        Assert.NotEqual(request.OperationId, registry.LastInvocation.ProbeInvocationId);
        Assert.NotEqual(value.Binding.OperationId, registry.LastInvocation.ProbeInvocationId);
        Assert.NotEqual(value.ContractMetadata.ActuatorOperationId, registry.LastInvocation.ProbeInvocationId);
        Assert.Equal(1, registry.ProbeCalls);
        Assert.Equal(2, store.ProbeReservationCalls);
        Assert.Equal(1, store.ProbeCommitCalls);
        Assert.Equal(attempt.ContentHash, replayed.EffectHead!.ContentHash);
    }

    [Fact]
    public async Task Probe_revalidates_the_canonical_case_head_and_skips_callback_after_an_intervening_advance()
    {
        var (value, attempt, inputValue, source) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        registry.ProbeResultFactory = _ => throw new InvalidOperationException("The stale reservation must not reach the callback.");
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input, registry, store);
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
            1,
            value.CaseId,
            value.Binding.ContentHash,
            "assessment-intervening",
            GovernedLoopEffectReconciliationAssessmentKind.Inconclusive,
            [],
            attempt.Payload.IntentHash,
            value.UpdatedAtUtc.AddSeconds(1),
            "An intervening assessment invalidated the reserved head.",
            string.Empty));
        var advanced = GovernedLoopEffectReconciliationContract.Create(
            value.CaseId,
            value.CaseVersion + 1,
            value.Binding,
            value.ContractMetadata,
            [source],
            value.ObservationHistory,
            [assessment],
            assessment.ContentHash,
            null,
            null,
            value.CaseReceiptHashes,
            value.ContentHash,
            value.OpenedAtUtc,
            assessment.AssessedAtUtc);
        store.BeforeCallbackValidationAction = () => store.SeedCase(advanced);

        var result = await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest("probe-operation-stale", Reference(value)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, result.Status);
        Assert.Equal(0, registry.ProbeCalls);
    }

    [Fact]
    public async Task Concurrent_probe_requests_share_one_reservation_and_one_callback()
    {
        var (value, attempt, inputValue, source) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        registry.ProbeResultFactory = _ => new GovernedLoopEffectReconciliationProbeInvocationResult(
            GovernedLoopEffectReconciliationProbeInvocationStatus.Ready,
            GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                "callback-observation",
                source.SourceId,
                source.ContentHash,
                GovernedLoopEffectReconciliationObservationKind.Evidence,
                source.ReliabilityPosture,
                GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
                "callback-evidence",
                attempt.PreconditionEvidenceHash,
                attempt.Payload.UpdatedAtUtc.AddSeconds(1),
                attempt.Payload.UpdatedAtUtc.AddSeconds(2),
                "No matching external effect exists.",
                string.Empty)));
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input, registry, store);
        var request = new GovernedLoopEffectReconciliationProbeRequest("probe-operation-concurrent", Reference(value));

        var results = await Task.WhenAll(service.ProbeAsync(request), service.ProbeAsync(request));

        Assert.Single(results, result => result.Status == GovernedLoopEffectReconciliationOperationStatus.Applied);
        Assert.Single(results, result => result.Status == GovernedLoopEffectReconciliationOperationStatus.Replayed);
        Assert.Equal(1, registry.ProbeCalls);
        Assert.Equal(1, store.ProbeCommitCalls);
    }

    [Fact]
    public async Task Probe_response_loss_after_durable_commit_replays_without_a_second_callback()
    {
        var (value, attempt, inputValue, source) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore { ThrowAfterProbeCommit = true };
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        registry.ProbeResultFactory = _ => new GovernedLoopEffectReconciliationProbeInvocationResult(
            GovernedLoopEffectReconciliationProbeInvocationStatus.Ready,
            GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                "response-loss-observation",
                source.SourceId,
                source.ContentHash,
                GovernedLoopEffectReconciliationObservationKind.Evidence,
                source.ReliabilityPosture,
                GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
                "response-loss-evidence",
                attempt.PreconditionEvidenceHash,
                attempt.Payload.UpdatedAtUtc.AddSeconds(1),
                attempt.Payload.UpdatedAtUtc.AddSeconds(2),
                "No matching external effect exists.",
                string.Empty)));
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input, registry, store);
        var request = new GovernedLoopEffectReconciliationProbeRequest("probe-operation-response-loss", Reference(value));

        var lost = await service.ProbeAsync(request);
        store.ThrowAfterProbeCommit = false;
        var replayed = await service.ProbeAsync(request);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, lost.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(1, registry.ProbeCalls);
    }

    [Fact]
    public async Task Probe_fails_closed_for_missing_ports_and_registered_statuses()
    {
        var (value, attempt, inputValue, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var basicStore = new GovernedLoopEffectReconciliationServiceStore();
        basicStore.SeedCase(value);
        basicStore.SeedEffect(attempt);
        var basicInput = ConfigureInput(inputValue, attempt, value);
        var basicService = new GovernedLoopEffectReconciliationService(basicStore, new GovernedLoopEffectReconciliationServiceAuthority(), basicInput);
        var request = new GovernedLoopEffectReconciliationProbeRequest("probe-operation-missing", Reference(value));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await basicService.ProbeAsync(request)).Status);

        foreach (var (registryStatus, expectedStatus) in new[]
        {
            (GovernedLoopEffectReconciliationProbeRegistryReadStatus.NotFound, GovernedLoopEffectReconciliationOperationStatus.NotFound),
            (GovernedLoopEffectReconciliationProbeRegistryReadStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid),
            (GovernedLoopEffectReconciliationProbeRegistryReadStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            (GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict, GovernedLoopEffectReconciliationOperationStatus.Unavailable),
            (GovernedLoopEffectReconciliationProbeRegistryReadStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)
        })
        {
            var store = new GovernedLoopEffectReconciliationServiceStore();
            store.SeedCase(value);
            store.SeedEffect(attempt);
            var input = ConfigureInput(inputValue, attempt, value);
            var authority = new GovernedLoopEffectReconciliationServiceAuthority();
            var registry = new RecordingGovernedLoopEffectReconciliationPorts
            {
                RegisteredContract = value.ContractMetadata,
                ForcedRegistryStatus = registryStatus
            };
            var service = new GovernedLoopEffectReconciliationService(store, authority, input, registry, store);

            Assert.Equal(expectedStatus, (await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest($"probe-operation-{registryStatus.ToString().ToLowerInvariant()}", Reference(value)))).Status);
        }
    }

    [Fact]
    public async Task Probe_rejects_invalid_case_state_input_and_source_postures_before_callback()
    {
        var (value, attempt, inputValue, source) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input, registry, store);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await service.ProbeAsync(null!)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest(value.Binding.OperationId, Reference(value)))).Status);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeRequest(value.ContractMetadata.ActuatorOperationId, Reference(value)));

        input.SetStatus(GovernedLoopEffectReconciliationInputReadStatus.NotFound);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest("probe-operation-input", Reference(value)))).Status);

        var missingSource = GovernedLoopEffectReconciliationContract.Create(value.CaseId, value.CaseVersion, value.Binding, value.ContractMetadata, [], value.ObservationHistory, value.AssessmentHistory, value.CurrentAssessmentHash, value.Disposition, value.Resolution, value.CaseReceiptHashes, null, value.OpenedAtUtc, value.UpdatedAtUtc);
        var missingSourceStore = new GovernedLoopEffectReconciliationServiceStore();
        missingSourceStore.SeedCase(missingSource);
        missingSourceStore.SeedEffect(attempt);
        var missingSourceInput = ConfigureInput(inputValue, attempt, missingSource);
        var missingSourceService = new GovernedLoopEffectReconciliationService(missingSourceStore, authority, missingSourceInput, registry, missingSourceStore);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await missingSourceService.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest("probe-operation-source-missing", Reference(missingSource)))).Status);

        var secondSource = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            source.SchemaVersion,
            source.CaseId,
            source.BindingHash,
            "source-probe-2",
            source.Kind,
            source.ReliabilityPosture,
            source.ReconciliationContractId,
            source.ReconciliationContractVersion,
            source.ReconciliationContractHash,
            source.RegistrationEvidenceHash,
            source.RegisteredAtUtc,
            source.RetiredAtUtc,
            string.Empty));
        var duplicateSources = GovernedLoopEffectReconciliationContract.Create(value.CaseId, value.CaseVersion, value.Binding, value.ContractMetadata, [source, secondSource], value.ObservationHistory, value.AssessmentHistory, value.CurrentAssessmentHash, value.Disposition, value.Resolution, value.CaseReceiptHashes, null, value.OpenedAtUtc, value.UpdatedAtUtc);
        var duplicateStore = new GovernedLoopEffectReconciliationServiceStore();
        duplicateStore.SeedCase(duplicateSources);
        duplicateStore.SeedEffect(attempt);
        var duplicateInput = ConfigureInput(inputValue, attempt, duplicateSources);
        var duplicateService = new GovernedLoopEffectReconciliationService(duplicateStore, authority, duplicateInput, registry, duplicateStore);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, (await duplicateService.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest("probe-operation-source-duplicate", Reference(duplicateSources)))).Status);
    }

    [Fact]
    public async Task Probe_records_bounded_uncertainty_when_registered_callback_times_out_or_fails()
    {
        foreach (var exception in new Exception[] { new TimeoutException("timeout"), new InvalidOperationException("failure") })
        {
            var (value, attempt, inputValue, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
            var store = new GovernedLoopEffectReconciliationServiceStore();
            store.SeedCase(value);
            store.SeedEffect(attempt);
            var input = ConfigureInput(inputValue, attempt, value);
            var authority = new GovernedLoopEffectReconciliationServiceAuthority();
            var registry = new RecordingGovernedLoopEffectReconciliationPorts
            {
                RegisteredContract = value.ContractMetadata,
                ProbeException = exception
            };
            var service = new GovernedLoopEffectReconciliationService(store, authority, input, registry, store);

            var result = await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest(exception is TimeoutException ? "probe-operation-timeout" : "probe-operation-failure", Reference(value)));

            Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, result.Status);
            Assert.Single(result.Case!.ObservationHistory);
            Assert.Equal(exception is TimeoutException ? GovernedLoopEffectReconciliationObservationKind.TimedOut : GovernedLoopEffectReconciliationObservationKind.Missing, result.Case.ObservationHistory.Single().Kind);
            Assert.Equal(attempt.ContentHash, result.EffectHead!.ContentHash);
        }
    }

    [Fact]
    public async Task Probe_maps_durable_reservation_and_commit_failures_without_callback_or_successor()
    {
        var reservationStatuses = new[]
        {
            (GovernedLoopEffectReconciliationProbeReservationStatus.Conflict, GovernedLoopEffectReconciliationOperationStatus.Conflict),
            (GovernedLoopEffectReconciliationProbeReservationStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid),
            (GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            (GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded, GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded),
            (GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired, GovernedLoopEffectReconciliationOperationStatus.RepairRequired),
            (GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)
        };
        foreach (var (forcedStatus, expectedStatus) in reservationStatuses)
        {
            var (value, attempt, inputValue, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
            var store = new GovernedLoopEffectReconciliationServiceStore { ForcedProbeReservationStatus = forcedStatus };
            store.SeedCase(value);
            store.SeedEffect(attempt);
            var input = ConfigureInput(inputValue, attempt, value);
            var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
            var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input, registry, store);

            var result = await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest($"probe-operation-reservation-{forcedStatus.ToString().ToLowerInvariant()}", Reference(value)));

            Assert.Equal(expectedStatus, result.Status);
            Assert.Equal(0, registry.ProbeCalls);
        }

        foreach (var forcedStatus in new[]
        {
            GovernedLoopEffectReconciliationProbeReservationStatus.Conflict,
            GovernedLoopEffectReconciliationProbeReservationStatus.Invalid,
            GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt,
            GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded,
            GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired,
            GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable
        })
        {
            var (value, attempt, inputValue, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
            var store = new GovernedLoopEffectReconciliationServiceStore { ForcedProbeCommitStatus = forcedStatus };
            store.SeedCase(value);
            store.SeedEffect(attempt);
            var input = ConfigureInput(inputValue, attempt, value);
            var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
            var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input, registry, store);

            var result = await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest($"probe-operation-commit-{forcedStatus.ToString().ToLowerInvariant()}", Reference(value)));

            Assert.Equal(forcedStatus switch
            {
                GovernedLoopEffectReconciliationProbeReservationStatus.Conflict => GovernedLoopEffectReconciliationOperationStatus.Conflict,
                GovernedLoopEffectReconciliationProbeReservationStatus.Invalid => GovernedLoopEffectReconciliationOperationStatus.Invalid,
                GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt => GovernedLoopEffectReconciliationOperationStatus.Corrupt,
                GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded => GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded,
                GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired => GovernedLoopEffectReconciliationOperationStatus.RepairRequired,
                _ => GovernedLoopEffectReconciliationOperationStatus.Unavailable
            }, result.Status);
            Assert.Equal(attempt.ContentHash, result.EffectHead?.ContentHash ?? attempt.ContentHash);
        }
    }

    [Fact]
    public async Task Probe_maps_reachable_dependency_failures_and_cancellation_without_invoking_an_unvalidated_callback()
    {
        var registryFailure = CreateProbeHarness();
        registryFailure.Registry.ThrowOnRegistryRead = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await registryFailure.Service.ProbeAsync(ProbeRequest(registryFailure.Value, "registry-throw"))).Status);

        var nullRegistry = CreateProbeHarness();
        nullRegistry.Registry.ReturnNullOnRegistryRead = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await nullRegistry.Service.ProbeAsync(ProbeRequest(nullRegistry.Value, "registry-null"))).Status);

        var reservationFailure = CreateProbeHarness();
        reservationFailure.Store.ThrowOnProbeReservation = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await reservationFailure.Service.ProbeAsync(ProbeRequest(reservationFailure.Value, "reservation-throw"))).Status);

        var nullReservation = CreateProbeHarness();
        nullReservation.Store.ReturnNullOnProbeReservation = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await nullReservation.Service.ProbeAsync(ProbeRequest(nullReservation.Value, "reservation-null"))).Status);

        var changedInput = CreateProbeHarness();
        changedInput.Input.BeforeRead = call =>
        {
            if (call == 2)
            {
                changedInput.Input.SetStatus(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
            }
        };
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await changedInput.Service.ProbeAsync(ProbeRequest(changedInput.Value, "changed-input"))).Status);

        var changedAuthority = CreateProbeHarness();
        changedAuthority.Authority.ResultFactory = request => new GovernedLoopEffectReconciliationAuthorizationResult(
            changedAuthority.Authority.Calls == 1 ? GovernedLoopEffectReconciliationAuthorizationStatus.Ready : GovernedLoopEffectReconciliationAuthorizationStatus.Denied,
            request.Purpose,
            request.Case,
            request.Binding,
            changedAuthority.Authority.Calls == 1 ? GovernedLoopEffectAttemptTestFixture.Hash('a') : null);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Denied, (await changedAuthority.Service.ProbeAsync(ProbeRequest(changedAuthority.Value, "changed-authority"))).Status);

        var changedRegistry = CreateProbeHarness();
        changedRegistry.Registry.RegistryReadResultFactory = call => call == 1
            ? new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, changedRegistry.Value.ContractMetadata, changedRegistry.Registry)
            : throw new IOException("The revalidation read is unavailable.");
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await changedRegistry.Service.ProbeAsync(ProbeRequest(changedRegistry.Value, "changed-registry"))).Status);

        foreach (var (validationStatus, expectedStatus) in new[]
        {
            (GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            (GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired, GovernedLoopEffectReconciliationOperationStatus.RepairRequired),
            (GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)
        })
        {
            var validation = CreateProbeHarness();
            validation.Store.ForcedCallbackValidationStatus = validationStatus;
            Assert.Equal(expectedStatus, (await validation.Service.ProbeAsync(ProbeRequest(validation.Value, $"validation-{validationStatus.ToString().ToLowerInvariant()}"))).Status);
            Assert.Equal(0, validation.Registry.ProbeCalls);
        }

        var validationFailure = CreateProbeHarness();
        validationFailure.Store.ThrowOnCallbackValidation = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await validationFailure.Service.ProbeAsync(ProbeRequest(validationFailure.Value, "validation-throw"))).Status);

        var commitFailure = CreateProbeHarness();
        commitFailure.Store.ThrowOnProbeCommit = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await commitFailure.Service.ProbeAsync(ProbeRequest(commitFailure.Value, "commit-throw"))).Status);

        var nullCommit = CreateProbeHarness();
        nullCommit.Store.ReturnNullOnProbeCommit = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await nullCommit.Service.ProbeAsync(ProbeRequest(nullCommit.Value, "commit-null"))).Status);

        var cancelled = CreateProbeHarness();
        using var cancellation = new CancellationTokenSource();
        cancelled.Store.BeforeCallbackValidationAction = cancellation.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.Service.ProbeAsync(ProbeRequest(cancelled.Value, "cancelled-callback"), cancellation.Token));
    }

    private static (
        GovernedLoopEffectReconciliationService Service,
        GovernedLoopEffectReconciliationServiceStore Store,
        GovernedLoopEffectReconciliationServiceInput Input,
        GovernedLoopEffectReconciliationServiceAuthority Authority,
        RecordingGovernedLoopEffectReconciliationPorts Registry,
        GovernedLoopEffectReconciliationCase Value) CreateProbeHarness()
    {
        var (value, attempt, inputValue, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ProbeCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var registry = new RecordingGovernedLoopEffectReconciliationPorts { RegisteredContract = value.ContractMetadata };
        return (new GovernedLoopEffectReconciliationService(store, authority, input, registry, store), store, input, authority, registry, value);
    }

    private static GovernedLoopEffectReconciliationProbeRequest ProbeRequest(GovernedLoopEffectReconciliationCase value, string suffix)
        => new($"probe-operation-{suffix}", Reference(value));

    private static GovernedLoopEffectReconciliationServiceInput ConfigureInput(GovernedActuatorInputEvidence inputValue, GovernedLoopEffectAttempt attempt, GovernedLoopEffectReconciliationCase value)
    {
        var input = new GovernedLoopEffectReconciliationServiceInput
        {
            Effect = attempt,
            Frontier = GovernedLoopEffectReconciliationApplicationTestFixture.ReviewBlockedFrontier(value, attempt),
            Input = inputValue
        };
        return input;
    }

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);
}
