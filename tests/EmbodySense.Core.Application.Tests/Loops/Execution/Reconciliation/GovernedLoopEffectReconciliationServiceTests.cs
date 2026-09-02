using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
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
