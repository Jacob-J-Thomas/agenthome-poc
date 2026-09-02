using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationServiceBoundaryTests
{
    [Theory]
    [InlineData(GovernedLoopEffectReconciliationCaseReadStatus.Unknown, GovernedLoopEffectReconciliationOperationStatus.Unknown)]
    [InlineData(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, GovernedLoopEffectReconciliationOperationStatus.NotFound)]
    [InlineData(GovernedLoopEffectReconciliationCaseReadStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid)]
    [InlineData(GovernedLoopEffectReconciliationCaseReadStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    [InlineData(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)]
    public async Task Read_maps_every_canonical_store_status_without_authorizing_or_disclosing_state(
        GovernedLoopEffectReconciliationCaseReadStatus storeStatus,
        GovernedLoopEffectReconciliationOperationStatus expectedStatus)
    {
        var (value, attempt, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore { ForcedReadStatus = storeStatus };
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority();
        var service = new GovernedLoopEffectReconciliationService(store, authority, new GovernedLoopEffectReconciliationServiceInput());

        var result = await service.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(value)));

        Assert.Equal(expectedStatus, result.Status);
        if (expectedStatus == GovernedLoopEffectReconciliationOperationStatus.Found)
        {
            Assert.NotNull(result.Case);
        }
        else
        {
            Assert.Null(result.Case);
        }

        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task Read_returns_found_case_only_for_the_exact_immutable_reference()
    {
        var (value, attempt, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), new GovernedLoopEffectReconciliationServiceInput());

        var found = await service.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(value)));
        var missing = await service.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(new GovernedLoopEffectReconciliationCaseReference(value.CaseId, value.CaseVersion, Hash('f'), value.Binding.ContentHash)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Found, found.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, missing.Status);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationAuthorizationStatus.Unknown, GovernedLoopEffectReconciliationOperationStatus.Unknown)]
    [InlineData(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, GovernedLoopEffectReconciliationOperationStatus.Denied)]
    [InlineData(GovernedLoopEffectReconciliationAuthorizationStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid)]
    [InlineData(GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    [InlineData(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)]
    public async Task Open_maps_every_authorization_status_and_never_mutates_without_ready_authority(
        GovernedLoopEffectReconciliationAuthorizationStatus authorityStatus,
        GovernedLoopEffectReconciliationOperationStatus expectedStatus)
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority { Status = authorityStatus };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);

        var result = await service.OpenAsync(OpenRequest(value));

        Assert.Equal(expectedStatus, result.Status);
        if (authorityStatus == GovernedLoopEffectReconciliationAuthorizationStatus.Ready)
        {
            Assert.NotNull(result.Case);
        }
        else
        {
            Assert.Null(result.Case);
            Assert.Null(store.CurrentCase);
            Assert.Null(input.LastCase);
        }
    }

    [Fact]
    public async Task Open_rejects_an_authority_result_that_changes_the_canonical_purpose()
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority
        {
            ResultFactory = request => new GovernedLoopEffectReconciliationAuthorizationResult(
                GovernedLoopEffectReconciliationAuthorizationStatus.Ready,
                "effect-reconciliation-other",
                request.Case,
                request.Binding,
                Hash('a'))
        };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);

        var result = await service.OpenAsync(OpenRequest(value));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, result.Status);
        Assert.Null(store.CurrentCase);
        Assert.Null(input.LastCase);
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.Unknown, GovernedLoopEffectReconciliationOperationStatus.Unknown)]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.NotFound, GovernedLoopEffectReconciliationOperationStatus.NotFound)]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.Conflict, GovernedLoopEffectReconciliationOperationStatus.Conflict)]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid)]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    [InlineData(GovernedLoopEffectReconciliationInputReadStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)]
    public async Task Open_maps_every_input_status_and_never_creates_a_case(
        GovernedLoopEffectReconciliationInputReadStatus inputStatus,
        GovernedLoopEffectReconciliationOperationStatus expectedStatus)
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        input.SetStatus(inputStatus);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input);

        var result = await service.OpenAsync(OpenRequest(value));

        Assert.Equal(expectedStatus, result.Status);
        if (inputStatus == GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            Assert.NotNull(result.Case);
        }
        else
        {
            Assert.Null(result.Case);
            Assert.Null(store.CurrentCase);
        }
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Unknown, GovernedLoopEffectReconciliationOperationStatus.Unknown)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, GovernedLoopEffectReconciliationOperationStatus.Replayed)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, GovernedLoopEffectReconciliationOperationStatus.Conflict)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded, GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded)]
    [InlineData(GovernedLoopEffectReconciliationCaseMutationStatus.RepairRequired, GovernedLoopEffectReconciliationOperationStatus.RepairRequired)]
    public async Task Assess_maps_every_canonical_mutation_status(
        GovernedLoopEffectReconciliationCaseMutationStatus mutationStatus,
        GovernedLoopEffectReconciliationOperationStatus expectedStatus)
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore { ForcedMutationStatus = mutationStatus };
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input);

        var result = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(value)));

        Assert.Equal(expectedStatus, result.Status);
        if (mutationStatus is GovernedLoopEffectReconciliationCaseMutationStatus.Replayed or GovernedLoopEffectReconciliationCaseMutationStatus.Conflict)
        {
            Assert.NotNull(result.Case);
            Assert.NotNull(result.EffectHead);
        }
        else
        {
            Assert.Null(result.Case);
            Assert.Null(result.EffectHead);
        }
    }

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationObservedOutcome.NotApplied, GovernedLoopEffectOutcome.NotApplied)]
    [InlineData(GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded, GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed, GovernedLoopEffectOutcome.Failed)]
    public async Task Each_proved_observation_reaches_only_its_typed_accepted_resolution(
        GovernedLoopEffectReconciliationObservedOutcome observedOutcome,
        GovernedLoopEffectOutcome expectedOutcome)
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationServiceCaseFactory.OpenCaseWithEvidence(observedOutcome);
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input);

        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(value)));
        var disposition = await service.DisposeAsync(new GovernedLoopEffectReconciliationDispositionRequest("dispose-1", Reference(assessed.Case!), DispositionFor(observedOutcome)));
        var resolved = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(disposition.Case!)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, disposition.Status);
        var replayed = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(disposition.Case!)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, resolved.Status);
        Assert.Equal(expectedOutcome, resolved.Case!.Resolution!.Outcome);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Conflicting_or_unknown_outcomes_can_only_be_quarantined_and_never_resolve()
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationServiceCaseFactory.OpenCaseWithEvidence(GovernedLoopEffectReconciliationObservedOutcome.NotApplied, conflicting: true);
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input);

        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(value)));
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
        var invalidAccept = await service.DisposeAsync(new GovernedLoopEffectReconciliationDispositionRequest("dispose-invalid", Reference(assessed.Case!), GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied));
        var quarantine = await service.DisposeAsync(new GovernedLoopEffectReconciliationDispositionRequest("dispose-1", Reference(assessed.Case!), GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved));
        var resolution = await service.ResolveAsync(new GovernedLoopEffectReconciliationResolutionRequest("resolve-1", Reference(quarantine.Case!)));

        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.Conflicting, assessed.Case!.AssessmentHistory.Single().Kind);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, invalidAccept.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, quarantine.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, resolution.Status);
        Assert.Null(resolution.EffectHead);
        Assert.Equal(attempt.ContentHash, store.CurrentEffect!.ContentHash);
    }

    [Fact]
    public async Task Dependency_exceptions_and_null_results_fail_closed_without_dispatch_or_mutation()
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore { ReturnNullOnRead = true };
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var authority = new GovernedLoopEffectReconciliationServiceAuthority { ThrowOnAuthorize = true };
        var service = new GovernedLoopEffectReconciliationService(store, authority, input);

        var read = await service.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(value)));
        var opened = await service.OpenAsync(OpenRequest(value));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, read.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, opened.Status);
        Assert.Null(store.CurrentCase);

        store.ReturnNullOnRead = false;
        store.ThrowOnMutation = true;
        authority.ThrowOnAuthorize = false;
        store.SeedCase(value);
        var assessed = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest("assess-1", Reference(value)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, assessed.Status);
        Assert.Equal(value.ContentHash, store.CurrentCase!.ContentHash);
    }

    [Fact]
    public async Task Oversized_assessment_detail_is_invalid_without_invoking_the_case_store_mutation()
    {
        var (value, attempt, inputValue) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var store = new GovernedLoopEffectReconciliationServiceStore();
        store.SeedCase(value);
        store.SeedEffect(attempt);
        var input = ConfigureInput(inputValue, attempt, value);
        var service = new GovernedLoopEffectReconciliationService(store, new GovernedLoopEffectReconciliationServiceAuthority(), input);

        var result = await service.AssessAsync(new GovernedLoopEffectReconciliationAssessmentRequest(
            "assess-1",
            Reference(value),
            new string('x', GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters + 1)));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, result.Status);
        Assert.Equal(value.ContentHash, store.CurrentCase!.ContentHash);
        Assert.Equal(0, store.MutationCalls);
    }

    private static GovernedLoopEffectReconciliationOpenRequest OpenRequest(GovernedLoopEffectReconciliationCase value)
        => new("open-1", value.CaseId, value.Binding, value.ContractMetadata, value.EvidenceSources, value.CaseReceiptHashes);

    private static GovernedLoopEffectReconciliationServiceInput ConfigureInput(GovernedActuatorInputEvidence inputValue, GovernedLoopEffectAttempt attempt, GovernedLoopEffectReconciliationCase value)
        => new()
        {
            Effect = attempt,
            Frontier = GovernedLoopEffectReconciliationApplicationTestFixture.ReviewBlockedFrontier(value, attempt),
            Input = inputValue
        };

    private static GovernedLoopEffectReconciliationDispositionKind DispositionFor(GovernedLoopEffectReconciliationObservedOutcome outcome)
        => outcome switch
        {
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied => GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded or GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed => GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            _ => GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved
        };

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static string Hash(char value) => GovernedLoopEffectAttemptTestFixture.Hash(value);
}
