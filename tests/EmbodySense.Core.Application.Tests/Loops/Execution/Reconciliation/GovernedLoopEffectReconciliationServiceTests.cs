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
        Assert.Equal(3, authority.Calls);
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
