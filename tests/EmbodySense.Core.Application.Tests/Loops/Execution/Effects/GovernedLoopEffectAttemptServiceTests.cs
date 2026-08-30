using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

public sealed class GovernedLoopEffectAttemptServiceTests
{
    [Fact]
    public async Task Preparation_is_server_derived_and_intent_is_durable_before_authority_and_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();
        operation.Execute = async (invocation, boundary, token) =>
        {
            Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, store.Current?.Payload.Phase);
            Assert.NotNull(store.Current?.DispatchAuthorityEvidenceHash);
            Assert.Equal("before-alpha", invocation.BeforeEvidenceId);
            var outcome = await boundary.CrossAsync(_ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")), token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };

        var result = await Service(catalog, store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Committed, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.Committed, result.Attempt?.Payload.Phase);
        Assert.Equal(GovernedLoopEffectAttemptTestFixture.HashInput("target:alpha"), result.Attempt?.TargetFingerprint);
        Assert.Equal(GovernedLoopEffectAttemptTestFixture.Hash('e'), result.Attempt?.PreconditionEvidenceHash);
        Assert.Equal("before-alpha", result.Attempt?.BeforeEvidenceId);
        Assert.Equal(1, operation.PrepareCalls);
        Assert.Equal(1, operation.ExecuteCalls);
        Assert.Equal(1, authority.Calls);
        Assert.Equal(4, store.ExchangeCalls);
    }

    [Fact]
    public async Task Expired_preparation_is_refreshed_once_before_any_intent_is_published()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.PreparationClaims.Enqueue(false);
        operation.PreparationClaims.Enqueue(true);
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")),
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Committed, result.Status);
        Assert.Equal(2, operation.PrepareCalls);
        Assert.Equal(2, operation.PreparationClaimCalls);
        Assert.Equal(2, store.BeginCalls);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Preparation_validator_fails_closed_when_frozen_store_has_no_atomic_claim_capability()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var inner = new InMemoryEffectAttemptStore();
        var store = new LegacyOnlyEffectAttemptStore(inner);
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary();

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(1, operation.PrepareCalls);
        Assert.Equal(0, operation.PreparationClaimCalls);
        Assert.Equal(0, inner.BeginCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Full_length_canonical_evidence_references_survive_preparation_boundary_and_commit()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var evidenceReference = "e" + new string('a', GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters - 1);
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(
            fixture.Descriptor,
            new GovernedActuatorPreparationEvidence(
                GovernedLoopEffectAttemptTestFixture.HashInput("target:alpha"),
                GovernedLoopEffectAttemptTestFixture.Hash('e'),
                evidenceReference));
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, evidenceReference, evidenceReference)),
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Committed, result.Status);
        Assert.Equal(evidenceReference, result.Attempt?.BeforeEvidenceId);
        Assert.Equal(evidenceReference, result.Attempt?.Payload.OutcomeEvidenceId);
        Assert.Equal(evidenceReference, result.Attempt?.AfterEvidenceId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("mismatch")]
    [InlineData("cancel")]
    public async Task Preparation_failure_leaves_zero_intent_authority_and_dispatch(string mode)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.PrepareMode = mode;
        var authority = new StubAuthorityBoundary();
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancel")
        {
            operation.CancelSource = cancellation;
        }

        if (mode == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request, cancellation.Token));
        }
        else
        {
            var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);
            Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        }

        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, store.ExchangeCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Adapter_validation_failure_never_prepares_or_retains_an_intent()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture))
        {
            ValidationFailure = "invalid",
            PrepareMode = "throw",
        };
        var authority = new StubAuthorityBoundary();

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, store.ExchangeCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Server_preparation_runs_after_resume_and_before_any_durable_intent()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture)) { PrepareMode = "null" };
        operation.BeforePrepare = () =>
        {
            Assert.Equal(1, store.ResumeCalls);
            Assert.Equal(0, store.BeginCalls);
            Assert.Equal(0, store.ExchangeCalls);
            Assert.Null(store.Current);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(1, operation.PrepareCalls);
        Assert.Equal(0, store.BeginCalls);
    }

    [Fact]
    public async Task Irreversible_capability_cannot_underclaim_authority_and_non_unattended_operation_requires_approval()
    {
        var irreversible = GovernedLoopEffectAttemptTestFixture.Create(CapabilitySideEffectClass.Irreversible);
        var underclaimed = WithAuthority(
            irreversible.Request,
            GovernedLoopEffectAttemptTestFixture.RequiredAuthority(
                irreversible.Request.CapabilityPin.DescriptorIdentity,
                CapabilitySideEffectClass.None));
        var operation = new StubOperation(irreversible.Descriptor, Preparation(irreversible));
        var store = new InMemoryEffectAttemptStore();
        var authority = new StubAuthorityBoundary();

        var denied = await Service(new StubCatalog(irreversible, operation), store, authority).ExecuteAsync(underclaimed);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, denied.Status);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);

        var attended = GovernedLoopEffectAttemptTestFixture.Create(unattended: false);
        var attendedOperation = new StubOperation(attended.Descriptor, Preparation(attended));
        var attendedStore = new InMemoryEffectAttemptStore();
        var approval = await Service(new StubCatalog(attended, attendedOperation), attendedStore, new StubAuthorityBoundary()).ExecuteAsync(attended.Request);
        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired, approval.Status);
        Assert.Equal(1, attendedStore.BeginCalls);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, approval.Attempt?.Payload.Phase);
        Assert.Null(approval.Attempt?.DispatchAuthorityEvidenceHash);
        Assert.Equal(0, attendedOperation.ExecuteCalls);
    }

    [Fact]
    public async Task Canonical_effect_attempt_service_replays_one_pre_dispatch_human_review_intent_without_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create(unattended: false);
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1)));

        var first = await service.ExecuteAsync(fixture.Request);
        var replay = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired, first.Status);
        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired, replay.Status);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, store.Current?.Payload.Phase);
        Assert.Equal(1, store.BeginCalls);
        Assert.Equal(0, store.ExchangeCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData(GovernedLoopEffectPhase.Committed, GovernedLoopEffectAttemptExecutionStatus.Replayed)]
    [InlineData(GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted)]
    [InlineData(GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired)]
    [InlineData(GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectAttemptExecutionStatus.Committed)]
    public async Task Restart_recovers_without_redispatch_and_terminal_phases_bypass_catalog(
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectAttemptExecutionStatus expected)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), phase);
        var store = new InMemoryEffectAttemptStore { Current = current };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var unavailable = new StubCatalog(fixture, operation) { Unavailable = true };
        var authority = new StubAuthorityBoundary();

        var result = await Service(unavailable, store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(expected, result.Status);
        Assert.Equal(phase == GovernedLoopEffectPhase.DispatchBoundaryReached ? 1 : 0, unavailable.ResolveCalls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(0, authority.Calls);
        Assert.True(result.Attempt?.Payload.Phase is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.DispatchNotStarted or GovernedLoopEffectPhase.ReconciliationRequired);
    }

    [Fact]
    public async Task Restart_adopts_exact_probed_outcome_and_commits_once_without_redispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var crossed = ToPhase(
            GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!),
            GovernedLoopEffectPhase.DispatchBoundaryReached);
        var store = new InMemoryEffectAttemptStore { Current = crossed };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture))
        {
            Probe = invocation =>
            {
                Assert.Equal(crossed.BeforeEvidenceId, invocation.BeforeEvidenceId);
                Assert.Equal(crossed.TargetFingerprint, invocation.TargetFingerprint);
                Assert.Equal(crossed.PreconditionEvidenceHash, invocation.PreconditionEvidenceHash);
                return new GovernedActuatorProbeResult(
                    GovernedActuatorProbePosture.OutcomeObserved,
                    new GovernedActuatorExternalOutcome(
                        GovernedLoopEffectOutcome.Succeeded,
                        "outcome-recovered",
                        "after-recovered"));
            },
        };
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();

        var recovered = await Service(catalog, store, authority).ExecuteAsync(fixture.Request);
        var replayed = await Service(catalog, store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Committed, recovered.Status);
        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Replayed, replayed.Status);
        Assert.Equal(GovernedLoopEffectPhase.Committed, store.Current?.Payload.Phase);
        Assert.Equal("outcome-recovered", store.Current?.Payload.OutcomeEvidenceId);
        Assert.Equal("after-recovered", store.Current?.AfterEvidenceId);
        Assert.Equal(1, operation.ProbeCalls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(1, catalog.ResolveCalls);
    }

    [Theory]
    [InlineData("indeterminate")]
    [InlineData("cancel")]
    [InlineData("throw")]
    public async Task Retained_dispatch_boundary_probe_failures_require_reconciliation_without_redispatch(string probeFailure)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.DispatchBoundaryReached),
        };
        using var cancellation = new CancellationTokenSource();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture))
        {
            Probe = _ => probeFailure switch
            {
                "cancel" => CancelProbe(cancellation),
                "throw" => throw new IOException("simulated probe failure"),
                _ => new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null),
            },
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request, cancellation.Token);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(1, operation.ProbeCalls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(0, store.BeginCalls);
    }

    [Fact]
    public async Task Retained_dispatch_boundary_exact_probe_requires_reconciliation_when_trusted_time_is_unavailable()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.DispatchBoundaryReached),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture))
        {
            Probe = _ => new GovernedActuatorProbeResult(
                GovernedActuatorProbePosture.OutcomeObserved,
                new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-recovered", "after-recovered")),
        };
        var service = new GovernedLoopEffectAttemptService(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary(), new ThrowingTimeProvider());

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, result.Attempt?.Payload.Phase);
        Assert.Equal(1, operation.ProbeCalls);
        Assert.Equal(0, store.ExchangeCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Retained_dispatch_boundary_exact_probe_requires_reconciliation_when_outcome_exchange_is_lost()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.DispatchBoundaryReached),
            FailExchangeCall = 1,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture))
        {
            Probe = _ => new GovernedActuatorProbeResult(
                GovernedActuatorProbePosture.OutcomeObserved,
                new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-recovered", "after-recovered")),
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(1, operation.ProbeCalls);
        Assert.Equal(2, store.ExchangeCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Resume_backpressure_is_projected_without_catalog_authority_or_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = _ => new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Backpressured),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();

        var result = await Service(catalog, store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Backpressured, result.Status);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Exact_changed_input_reuse_conflicts_before_catalog_even_when_catalog_is_unavailable()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore { Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.Committed) };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation) { Unavailable = true };

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request with { InputJson = "{\"target\":\"beta\"}" });

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Conflict, result.Status);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData("stop", GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted)]
    [InlineData("throw", GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted)]
    [InlineData("cancel", GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted)]
    [InlineData("cross-throw", GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired)]
    [InlineData("cross-cancel", GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired)]
    public async Task Adapter_pre_and_post_boundary_outcomes_are_classified_without_unsafe_retry(
        string mode,
        GovernedLoopEffectAttemptExecutionStatus expected)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture)) { ExecuteMode = mode };
        var authority = new StubAuthorityBoundary();

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted ? GovernedLoopEffectPhase.DispatchNotStarted : GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("outcome-substitution", false)]
    [InlineData("outcome-evidence-substitution", false)]
    [InlineData("after-evidence-substitution", false)]
    [InlineData("dispatch-not-started-substitution", false)]
    [InlineData("null-adapter-result", false)]
    public async Task Only_the_exact_callback_outcome_can_be_committed(
        string mode,
        bool expectedCommit)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")),
                token);
            return mode switch
            {
                "exact" => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome),
                "outcome-substitution" => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome with { Outcome = GovernedLoopEffectOutcome.Failed }),
                "outcome-evidence-substitution" => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome with { OutcomeEvidenceId = "outcome-beta" }),
                "after-evidence-substitution" => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome with { AfterEvidenceId = "after-beta" }),
                "dispatch-not-started-substitution" => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null),
                "null-adapter-result" => null!,
                _ => throw new InvalidOperationException("Unsupported test mode."),
            };
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(
            expectedCommit ? GovernedLoopEffectAttemptExecutionStatus.Committed : GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            expectedCommit ? GovernedLoopEffectPhase.Committed : GovernedLoopEffectPhase.ReconciliationRequired,
            result.Attempt?.Payload.Phase);
        Assert.Equal(expectedCommit ? "outcome-alpha" : null, result.Attempt?.Payload.OutcomeEvidenceId);
        Assert.Equal(expectedCommit ? "after-alpha" : null, result.Attempt?.AfterEvidenceId);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Outcome_persistence_failure_after_dispatch_is_durably_reconciliation_required()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            FailExchangeCall = 3,
            ExchangeFailureStatus = GovernedLoopEffectAttemptStoreStatus.Unavailable,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")),
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, store.Current?.Payload.Phase);
        Assert.Equal(1, operation.ExecuteCalls);
        Assert.Equal(4, store.ExchangeCalls);
    }

    [Theory]
    [InlineData("unknown-outcome")]
    [InlineData("malformed-outcome-evidence")]
    [InlineData("malformed-after-evidence")]
    [InlineData("missing-after-evidence")]
    [InlineData("null-outcome")]
    public async Task Malformed_callback_outcome_requires_reconciliation_without_committing_evidence(string mode)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(mode switch
                {
                    "unknown-outcome" => new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.OutcomeUnknown, "outcome-alpha", "after-alpha"),
                    "malformed-outcome-evidence" => new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "INVALID", "after-alpha"),
                    "malformed-after-evidence" => new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "INVALID"),
                    "missing-after-evidence" => new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", null),
                    "null-outcome" => null!,
                    _ => throw new InvalidOperationException("Unsupported test mode."),
                }),
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Null(result.Attempt?.Payload.OutcomeEvidenceId);
        Assert.Null(result.Attempt?.AfterEvidenceId);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UPPERCASEINVALIDHASH____________________________________________")]
    public async Task Hostile_prior_direct_signal_never_creates_false_dispatch_not_started(string? storedHash)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { PriorDirectHash = storedHash };
        if (storedHash is null)
        {
            authority.SignalNullPriorDirect();
        }

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.NotEqual(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Replayed_denial_is_finalized_as_dispatch_not_started_without_attaching_direct_authority()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { ReplayDenied = true };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Null(result.Attempt?.DispatchAuthorityEvidenceHash);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Hostile_dependency_detail_is_never_projected_from_retained_intent()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore { Current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!) };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation)
        {
            Unavailable = true,
            UnavailableDetail = "secret-canary-hostile-catalog-detail",
        };

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.DoesNotContain("secret-canary", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Inexact_authority_callback_fails_before_adapter_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary
        {
            DecisionMutator = decision => decision with { CorrelationId = "wrong-correlation" },
        };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, store.Current?.Payload.Phase);
        Assert.Null(store.Current?.DispatchAuthorityEvidenceHash);
    }

    [Fact]
    public async Task Fabricated_direct_authority_result_without_callback_never_dispatches()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { FabricateDirectWithoutCommit = true };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, store.Current?.Payload.Phase);
        Assert.Null(store.Current?.DispatchAuthorityEvidenceHash);
    }

    [Fact]
    public async Task Cancellation_during_post_intent_catalog_revalidation_records_dispatch_not_started()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        using var cancellation = new CancellationTokenSource();
        var catalog = new StubCatalog(fixture, operation)
        {
            CancelOnResolveCall = 2,
            CancelSource = cancellation,
        };

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request, cancellation.Token);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Trusted_time_failure_during_recovery_returns_evidence_unavailable_without_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.OutcomeObserved),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new ThrowingTimeProvider());

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData(3, GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, GovernedLoopEffectPhase.DispatchBoundaryReached)]
    [InlineData(4, GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, GovernedLoopEffectPhase.OutcomeObserved)]
    public async Task Trusted_time_failure_after_external_boundary_preserves_the_exact_durable_posture(
        int validTimeReads,
        GovernedLoopEffectAttemptExecutionStatus expectedStatus,
        GovernedLoopEffectPhase expectedPhase)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            var outcome = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")),
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        };
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new FailingAfterTimeProvider(validTimeReads));

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedPhase, result.Attempt?.Payload.Phase);
        Assert.Equal(expectedPhase, store.Current?.Payload.Phase);
        Assert.Equal(1, operation.ExecuteCalls);
        Assert.DoesNotContain("before external dispatch", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trusted_time_failure_before_intent_returns_structured_failure_without_preparation_or_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new ThrowingTimeProvider());

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(0, store.BeginCalls);
    }

    [Fact]
    public async Task Regressing_trusted_time_after_intent_fails_closed_without_exception_or_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new RegressingTimeProvider());

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Over_bound_authority_is_rejected_before_any_protected_port()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var excessive = new AuthorityCeiling(
            Enumerable.Repeat(
                fixture.Request.CapabilityPin.DescriptorIdentity,
                AuthorityContractLimits.MaxCapabilitiesPerCeiling + 1).ToArray(),
            [],
            1,
            CapabilitySideEffectClass.LocalReversible,
            false,
            false,
            false);
        var request = WithAuthority(fixture.Request, excessive);
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();

        var result = await Service(catalog, store, authority).ExecuteAsync(request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ResumeCalls);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Malformed_admission_pin_is_rejected_before_any_protected_port()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var request = fixture.Request with
        {
            CapabilityPin = fixture.Request.CapabilityPin with { Provenance = null! },
        };
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();

        var result = await Service(catalog, store, authority).ExecuteAsync(request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ResumeCalls);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.PrepareCalls);
    }

    [Theory]
    [InlineData("{\"target\":\"\\uD800\"}")]
    [InlineData("{\"target\":\"\\uDC00\"}")]
    [InlineData("{\"\\uD800\":\"alpha\"}")]
    [InlineData("{\"\\uDC00\":\"alpha\"}")]
    public async Task Malformed_escaped_surrogates_are_invalid_before_any_protected_port(string inputJson)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);
        var authority = new StubAuthorityBoundary();

        var result = await Service(catalog, store, authority).ExecuteAsync(fixture.Request with { InputJson = inputJson });

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ResumeCalls);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Null_request_is_rejected_before_reading_any_protected_port()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var catalog = new StubCatalog(fixture, operation);

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(null!);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ResumeCalls);
        Assert.Equal(0, catalog.ResolveCalls);
        Assert.Equal(0, operation.PrepareCalls);
    }

    [Fact]
    public async Task Incoherent_absent_store_result_is_evidence_unavailable_and_lease_is_disposed()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var lease = new TestEffectAttemptLease();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.NotFound
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.NotFound, null, lease)
                : result,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.True(lease.Disposed);
        Assert.Equal(0, operation.PrepareCalls);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAttemptStoreStatus.Conflict, GovernedLoopEffectAttemptExecutionStatus.Conflict)]
    [InlineData(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, GovernedLoopEffectAttemptExecutionStatus.OperationInProgress)]
    [InlineData(GovernedLoopEffectAttemptStoreStatus.Backpressured, GovernedLoopEffectAttemptExecutionStatus.Backpressured)]
    [InlineData(GovernedLoopEffectAttemptStoreStatus.Unavailable, GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable)]
    public async Task Begin_store_postures_are_projected_without_dispatch(
        GovernedLoopEffectAttemptStoreStatus beginStatus,
        GovernedLoopEffectAttemptExecutionStatus expectedStatus)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Created
                ? new GovernedLoopEffectAttemptStoreResult(beginStatus)
                : result,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary();

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Prior_direct_authority_evidence_enters_reconciliation_without_redispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { PriorDirectHash = GovernedLoopEffectAttemptTestFixture.Hash('f') };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(GovernedLoopEffectAttemptTestFixture.Hash('f'), result.Attempt?.DispatchAuthorityEvidenceHash);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData("resume", GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable)]
    [InlineData("begin", GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable)]
    [InlineData("catalog", GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable)]
    public async Task Protected_dependency_exceptions_fail_closed_without_dispatch(string dependency, GovernedLoopEffectAttemptExecutionStatus expectedStatus)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        IGovernedLoopEffectAttemptStore store = dependency switch
        {
            "resume" => new ThrowingEffectAttemptStore(throwOnResume: true, throwOnBegin: false),
            "begin" => new ThrowingEffectAttemptStore(throwOnResume: false, throwOnBegin: true),
            _ => new InMemoryEffectAttemptStore(),
        };
        IGovernedActuatorCatalogResolver catalog = dependency == "catalog"
            ? new ThrowingCatalog()
            : new StubCatalog(fixture, operation);

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Begin_result_that_changes_the_prepared_attempt_is_treated_as_unavailable()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = result => result.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed
                && result.Attempt is not null
                    ? new GovernedLoopEffectAttemptStoreResult(result.Status, result.Attempt with { ContentHash = GovernedLoopEffectAttemptTestFixture.Hash('a') })
                    : result,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(1, store.BeginCalls);
        Assert.Equal(0, store.ExchangeCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Retained_operation_in_progress_with_an_owner_is_impossible_evidence()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var lease = new TestEffectAttemptLease();
        var store = new InMemoryEffectAttemptStore
        {
            Current = current,
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Replayed
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, current, lease)
                : result,
        };

        var result = await Service(new StubCatalog(fixture, new StubOperation(fixture.Descriptor, Preparation(fixture))), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task Retained_terminal_operation_with_an_owner_is_impossible_evidence()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.Committed);
        var lease = new TestEffectAttemptLease();
        var store = new InMemoryEffectAttemptStore
        {
            Current = current,
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Replayed
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Replayed, current, lease)
                : result,
        };

        var result = await Service(new StubCatalog(fixture, new StubOperation(fixture.Descriptor, Preparation(fixture))), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task Retained_unfinished_operation_without_an_owner_is_evidence_unavailable()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var store = new InMemoryEffectAttemptStore
        {
            Current = current,
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Replayed
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Replayed, current)
                : result,
        };

        var result = await Service(new StubCatalog(fixture, new StubOperation(fixture.Descriptor, Preparation(fixture))), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
    }

    [Fact]
    public async Task Retained_intent_with_changed_preparation_is_stopped_before_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture) with { BeforeEvidenceId = "before-beta" });

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Evidence_failure_after_the_external_boundary_requires_reconciliation()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            var crossed = await boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha")),
                token);
            Assert.Equal(GovernedLoopEffectOutcome.Succeeded, crossed.Outcome);
            throw new InvalidOperationException("adapter-failure-after-boundary");
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Catalog_revalidation_change_stops_before_authority_and_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var initial = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var changedDescriptor = fixture.Descriptor with { RequiresBeforeEvidence = false };
        changedDescriptor = changedDescriptor with { ContentHash = GovernedActuatorOperationContract.Compute(changedDescriptor) };
        var changed = new StubOperation(changedDescriptor, Preparation(fixture));
        var catalog = new RevalidatingCatalog(fixture, initial, changed);
        var store = new InMemoryEffectAttemptStore();
        var authority = new StubAuthorityBoundary();

        var result = await Service(catalog, store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, changed.ExecuteCalls);
    }

    [Fact]
    public async Task Initial_catalog_unavailability_is_reported_before_intent_publication()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation) { Unavailable = true }, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, result.Status);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Incoherent_catalog_resolution_is_evidence_unavailable_before_intent_publication()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new IncoherentCatalog(), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, result.Status);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Unknown_catalog_status_is_rejected_before_intent_publication()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new UnknownCatalog(), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, result.Status);
        Assert.Equal(0, store.BeginCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Not_found_begin_result_is_incoherent_and_never_dispatches()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Created
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.NotFound)
                : result,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Authority_attachment_persistence_failure_stops_before_adapter_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore { FailExchangeCall = 1 };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Replayed_in_progress_without_an_owner_is_projected_without_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var store = new InMemoryEffectAttemptStore
        {
            Current = current,
            MutateResult = result => result.Status == GovernedLoopEffectAttemptStoreStatus.Replayed
                ? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, current)
                : result,
        };

        var result = await Service(new StubCatalog(fixture, new StubOperation(fixture.Descriptor, Preparation(fixture))), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.OperationInProgress, result.Status);
        Assert.Equal(current, result.Attempt);
    }

    [Fact]
    public async Task Null_catalog_resolution_is_projected_as_catalog_unavailable()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var result = await Service(new NullCatalog(), new InMemoryEffectAttemptStore(), new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, result.Status);
    }

    [Fact]
    public async Task Malformed_authority_attachment_result_fails_closed_before_external_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            MutateResult = result => result.Attempt?.PreviousContentHash is not null
                ? new GovernedLoopEffectAttemptStoreResult(result.Status, result.Attempt with { ContentHash = GovernedLoopEffectAttemptTestFixture.Hash('a') })
                : result,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Dispatch_boundary_is_single_use_and_second_cross_requires_reconciliation()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var callbackInvocations = 0;
        operation.Execute = async (_, boundary, token) =>
        {
            var callback = new Func<CancellationToken, Task<GovernedActuatorExternalOutcome>>(
                _ =>
                {
                    callbackInvocations++;
                    return Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha"));
                });
            await boundary.CrossAsync(callback, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.CrossAsync(callback, token));
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
        Assert.Equal(1, callbackInvocations);
    }

    [Fact]
    public async Task Dispatch_boundary_persistence_exception_fails_closed_before_external_callback()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore { FailExchangeCall = 2 };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var callbackInvoked = false;
        operation.Execute = async (_, boundary, token) =>
        {
            await Assert.ThrowsAsync<Exception>(() => boundary.CrossAsync(
                _ =>
                {
                    callbackInvoked = true;
                    return Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha"));
                },
                token));
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Missing_authority_result_fails_closed_without_adapter_dispatch()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { ReturnNull = true };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Authority_without_adapter_result_requires_reconciliation_evidence()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var authority = new StubAuthorityBoundary { ReturnDirectWithoutResult = true };

        var result = await Service(new StubCatalog(fixture, operation), store, authority).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Theory]
    [InlineData(0, GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, GovernedLoopEffectPhase.ReconciliationRequired, 3)]
    [InlineData(1, GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, GovernedLoopEffectPhase.IntentPrepared, 1)]
    [InlineData(2, GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, GovernedLoopEffectPhase.IntentPrepared, 2)]
    public async Task Adapter_without_external_outcome_records_only_safe_reconciliation_posture(
        int failureMode,
        GovernedLoopEffectAttemptExecutionStatus expectedStatus,
        GovernedLoopEffectPhase expectedPhase,
        int expectedExchangeCalls)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore
        {
            FailExchangeCall = failureMode == 2 ? 2 : null,
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = (_, _, _) => Task.FromResult(new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, null));
        TimeProvider timeProvider = failureMode == 1 ? new FailingAfterTimeProvider(2) : new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1));

        var result = await new GovernedLoopEffectAttemptService(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary(), timeProvider).ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedPhase, result.Attempt?.Payload.Phase);
        Assert.NotNull(result.Attempt?.DispatchAuthorityEvidenceHash);
        Assert.Same(store.Current, result.Attempt);
        Assert.Equal(expectedExchangeCalls, store.ExchangeCalls);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Cancellation_during_retained_preparation_records_dispatch_not_started()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        using var cancellation = new CancellationTokenSource();
        var catalog = new StubCatalog(fixture, operation)
        {
            CancelOnResolveCall = 1,
            CancelSource = cancellation,
        };

        var result = await Service(catalog, store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request, cancellation.Token);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchNotStarted, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Trusted_time_failure_during_retained_boundary_recovery_preserves_reconciliation_posture()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var store = new InMemoryEffectAttemptStore
        {
            Current = ToPhase(GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!), GovernedLoopEffectPhase.DispatchBoundaryReached),
        };
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var service = new GovernedLoopEffectAttemptService(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary(), new ThrowingTimeProvider());

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, result.Attempt?.Payload.Phase);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Malformed_external_callback_outcome_requires_reconciliation()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.OutcomeUnknown, "outcome-alpha", "after-alpha")),
                token));
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, result.Status);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.Attempt?.Payload.Phase);
    }

    [Fact]
    public async Task Null_external_callback_is_rejected_before_crossing_the_boundary()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        operation.Execute = async (_, boundary, token) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => boundary.CrossAsync(null!, token));
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
        };

        var result = await Service(new StubCatalog(fixture, operation), store, new StubAuthorityBoundary()).ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(2, store.ExchangeCalls);
    }

    [Fact]
    public async Task Trusted_time_failure_at_dispatch_boundary_fails_closed_without_external_callback()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var store = new InMemoryEffectAttemptStore();
        var operation = new StubOperation(fixture.Descriptor, Preparation(fixture));
        var callbackInvoked = false;
        operation.Execute = async (_, boundary, token) =>
        {
            await boundary.CrossAsync(
                _ =>
                {
                    callbackInvoked = true;
                    return Task.FromResult(new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "outcome-alpha", "after-alpha"));
                },
                token);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, null);
        };
        var service = new GovernedLoopEffectAttemptService(
            new StubCatalog(fixture, operation),
            store,
            new StubAuthorityBoundary(),
            new FailingAfterTimeProvider(2));

        var result = await service.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal(1, operation.ExecuteCalls);
    }

    private static GovernedLoopEffectAttemptService Service(
        IGovernedActuatorCatalogResolver catalog,
        IGovernedLoopEffectAttemptStore store,
        IGovernedLoopEffectAuthorityDecisionBoundary authority)
        => new(catalog, store, authority, new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1)));

    private static GovernedActuatorPreparationEvidence Preparation(GovernedLoopEffectAttemptTestFixture fixture)
        => new(GovernedLoopEffectAttemptTestFixture.HashInput("target:alpha"), GovernedLoopEffectAttemptTestFixture.Hash('e'), "before-alpha");

    private static GovernedLoopEffectAttemptRequest WithAuthority(
        GovernedLoopEffectAttemptRequest request,
        AuthorityCeiling authority)
        => new(
            request.AdmissionReceipt,
            request.ExecutionBinding,
            request.GraphArtifact,
            request.NodeId,
            request.NodeAttempt,
            request.CapabilityPin,
            request.ActuatorOperationId,
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            request.InputJson,
            authority,
            request.CorrelationId);

    private static GovernedActuatorProbeResult CancelProbe(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
    }

    private static GovernedLoopEffectAttempt ToPhase(GovernedLoopEffectAttempt prepared, GovernedLoopEffectPhase phase)
    {
        var authority = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, GovernedLoopEffectAttemptTestFixture.Hash('f'), GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(1));
        if (phase == GovernedLoopEffectPhase.DispatchNotStarted)
        {
            return GovernedLoopEffectAttemptContract.Advance(authority, phase, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(2));
        }
        var crossed = GovernedLoopEffectAttemptContract.Advance(authority, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(2));
        if (phase == GovernedLoopEffectPhase.DispatchBoundaryReached)
        {
            return crossed;
        }
        var observed = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-alpha", "after-alpha", GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(3));
        return phase == GovernedLoopEffectPhase.OutcomeObserved
            ? observed
            : GovernedLoopEffectAttemptContract.Advance(observed, GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-alpha", "after-alpha", GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(4));
    }

    private sealed class StubCatalog(GovernedLoopEffectAttemptTestFixture fixture, IGovernedActuatorOperation operation) : IGovernedActuatorCatalogResolver
    {
        internal bool Unavailable { get; set; }
        internal string UnavailableDetail { get; set; } = "unavailable";
        internal int? CancelOnResolveCall { get; set; }
        internal CancellationTokenSource? CancelSource { get; set; }
        internal int ResolveCalls { get; private set; }
        public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(CapabilityAdmissionPin pin, string operationId, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            if (ResolveCalls == CancelOnResolveCall)
            {
                CancelSource!.Cancel();
                throw new OperationCanceledException(CancelSource.Token);
            }
            return Task.FromResult(Unavailable
                ? new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, null, fixture.Descriptor, null, UnavailableDetail)
                : new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.Active, fixture.Capability, fixture.Descriptor, operation, "active"));
        }
    }

    private sealed class LegacyOnlyEffectAttemptStore(InMemoryEffectAttemptStore inner) : IGovernedLoopEffectAttemptStore
    {
        public Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(
            string operationId,
            long effectGeneration,
            CancellationToken cancellationToken = default)
            => inner.ResumeAsync(operationId, effectGeneration, cancellationToken);

        public Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(
            GovernedLoopEffectAttempt prepared,
            CancellationToken cancellationToken = default)
            => inner.BeginAsync(prepared, cancellationToken);

        public Task<GovernedLoopEffectAttemptStoreResult> CompareExchangeAsync(
            string expectedContentHash,
            GovernedLoopEffectAttempt replacement,
            IGovernedLoopEffectAttemptLease lease,
            CancellationToken cancellationToken = default)
            => inner.CompareExchangeAsync(expectedContentHash, replacement, lease, cancellationToken);
    }

    private sealed class StubOperation(GovernedActuatorOperationDescriptor descriptor, GovernedActuatorPreparationEvidence preparation) : IGovernedActuatorOperation, IGovernedActuatorOutcomeProbe, IGovernedActuatorPreparationValidator
    {
        internal Func<GovernedActuatorInvocation, IGovernedActuatorDispatchBoundary, CancellationToken, Task<GovernedActuatorAdapterResult>>? Execute { get; set; }
        internal string PrepareMode { get; set; } = "ok";
        internal string ExecuteMode { get; set; } = "custom";
        internal string? ValidationFailure { get; set; }
        internal CancellationTokenSource? CancelSource { get; set; }
        internal Action? BeforePrepare { get; set; }
        internal Func<GovernedActuatorInvocation, GovernedActuatorProbeResult>? Probe { get; set; }
        internal int PrepareCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        internal int ProbeCalls { get; private set; }
        internal int PreparationClaimCalls { get; private set; }
        internal Queue<bool> PreparationClaims { get; } = [];
        public GovernedActuatorOperationDescriptor Descriptor { get; } = descriptor;
        public string? ValidateInput(GovernedActuatorInputEvidence input) => ValidationFailure;
        public Task<GovernedActuatorPreparationEvidence?> PrepareAsync(GovernedActuatorInputEvidence input, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            BeforePrepare?.Invoke();
            return PrepareMode switch
            {
                "null" => Task.FromResult<GovernedActuatorPreparationEvidence?>(null),
                "throw" => throw new InvalidOperationException("secret-canary-preparation"),
                "mismatch" => Task.FromResult<GovernedActuatorPreparationEvidence?>(preparation with { TargetFingerprint = "not-a-canonical-hash" }),
                "cancel" => Cancel(),
                _ => Task.FromResult<GovernedActuatorPreparationEvidence?>(preparation),
            };
        }
        public async Task<GovernedActuatorAdapterResult> ExecuteAsync(GovernedActuatorInvocation invocation, IGovernedActuatorDispatchBoundary dispatchBoundary, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            if (Execute is not null)
            {
                return await Execute(invocation, dispatchBoundary, cancellationToken);
            }
            if (ExecuteMode == "stop") return new(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
            if (ExecuteMode == "throw") throw new InvalidOperationException("secret-canary-throw");
            if (ExecuteMode == "cancel") throw new OperationCanceledException(cancellationToken);
            if (ExecuteMode == "cross-throw")
            {
                _ = await dispatchBoundary.CrossAsync(_ => throw new InvalidOperationException("secret-canary-cross"), cancellationToken);
                throw new InvalidOperationException("The throwing boundary callback unexpectedly returned.");
            }
            if (ExecuteMode == "cross-cancel")
            {
                _ = await dispatchBoundary.CrossAsync(_ => throw new OperationCanceledException(cancellationToken), cancellationToken);
                throw new InvalidOperationException("The cancelling boundary callback unexpectedly returned.");
            }
            return new(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
        }
        public Task<bool> IsPreparationCurrentAsync(GovernedActuatorInputEvidence input, GovernedActuatorPreparationEvidence candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparationClaimCalls++;
            return Task.FromResult(PreparationClaims.Count == 0 || PreparationClaims.Dequeue());
        }
        public Task<GovernedActuatorProbeResult> ProbeAsync(GovernedActuatorInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCalls++;
            return Task.FromResult(Probe?.Invoke(invocation)
                ?? new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null));
        }
        private Task<GovernedActuatorPreparationEvidence?> Cancel()
        {
            CancelSource!.Cancel();
            throw new OperationCanceledException(CancelSource.Token);
        }
    }

    private sealed class StubAuthorityBoundary : IGovernedLoopEffectAuthorityDecisionBoundary
    {
        internal int Calls { get; private set; }
        internal string? PriorDirectHash { get; set; }
        internal Func<GovernedLoopEffectAuthorityDecision, GovernedLoopEffectAuthorityDecision>? DecisionMutator { get; set; }
        internal bool FabricateDirectWithoutCommit { get; set; }
        internal bool ReturnNull { get; set; }
        internal bool ReturnDirectWithoutResult { get; set; }
        internal bool ReplayDenied { get; set; }
        public ICapabilityAuthorityTransaction AuthorityTransaction => throw new InvalidOperationException("The service test boundary has no production workspace authority transaction.");

        public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(GovernedLoopEffectAuthorityRequest request, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
            => ExecuteWithDecisionAsync(request, (_, token) => commit(token), cancellationToken);
        public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(GovernedLoopEffectAuthorityRequest request, Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ReturnNull)
            {
                return null!;
            }
            var decision = Decision(request);
            decision = DecisionMutator?.Invoke(decision) ?? decision;
            if (ReplayDenied)
            {
                decision = Decision(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantRevoked);
                return new(
                    GovernedLoopEffectAuthorityExecutionStatus.Decided,
                    decision,
                    GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
                    false,
                    default,
                    "denied",
                    decision.ContentHash);
            }
            if (FabricateDirectWithoutCommit)
            {
                var fabricated = (TResult)(object)new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null);
                return new(
                    GovernedLoopEffectAuthorityExecutionStatus.Decided,
                    decision,
                    GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                    true,
                    fabricated,
                    "fabricated",
                    decision.ContentHash);
            }
            if (ReturnDirectWithoutResult)
            {
                return new(
                    GovernedLoopEffectAuthorityExecutionStatus.Decided,
                    decision,
                    GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                    true,
                    default,
                    "direct-without-result",
                    decision.ContentHash);
            }
            if (PriorDirectHash is not null || PriorDirectHash == null && _returnPriorDirect)
            {
                return new(
                    GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
                    Decision(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous),
                    GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
                    false,
                    default,
                    "ambiguous",
                    PriorDirectHash);
            }
            var result = await commit(decision, cancellationToken);
            return new(GovernedLoopEffectAuthorityExecutionStatus.Decided, decision, GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, true, result, "direct", decision.ContentHash);
        }
        private bool _returnPriorDirect => PriorDirectHash is not null || _explicitNull;
        private bool _explicitNull;
        internal void SignalNullPriorDirect() => _explicitNull = true;
        private static GovernedLoopEffectAuthorityDecision Decision(
            GovernedLoopEffectAuthorityRequest request,
            GovernedLoopEffectAuthorityDisposition disposition = GovernedLoopEffectAuthorityDisposition.Direct,
            GovernedLoopEffectAuthorityReason reason = GovernedLoopEffectAuthorityReason.ActiveExact)
        {
            var receipt = request.AdmissionReceipt;
            var proof = new GovernedLoopEffectAuthorityProof(
                GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
                receipt.Intent.AuthorityGrant,
                new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
                AuthorityGrantLifecycleStatus.Active,
                GovernedLoopEffectAuthorityGrantPosture.Active,
                receipt.Evidence.GrantBoundary,
                receipt.Evidence.EffectiveAuthority,
                receipt.Evidence.CapabilityAdmission.Pins,
                [],
                receipt.Evidence.GrantDependencyEvidenceHash);
            return GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
                GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
                request.ExecutionBinding.RunId,
                request.ExecutionBinding.ExecutionGeneration,
                request.NodeId,
                request.NodeAttempt,
                request.EffectOperationId,
                request.CorrelationId,
                request.BoundaryKind,
                receipt.ContentHash,
                proof,
                proof,
                request.RequiredAuthority,
                disposition == GovernedLoopEffectAuthorityDisposition.Direct ? request.RequiredAuthority : AuthorityCeilingIntersection.EmptyCeiling(),
                request.RequiredCapabilityPins,
                disposition,
                reason,
                GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1),
                string.Empty));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("trusted-time-unavailable");
    }

    private sealed class FailingAfterTimeProvider(int validReads) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow()
            => Interlocked.Increment(ref _reads) <= validReads
                ? GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1).AddTicks(_reads)
                : throw new InvalidOperationException("trusted-time-unavailable");
    }

    private sealed class RegressingTimeProvider : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow()
            => Interlocked.Increment(ref _reads) == 1
                ? GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1)
                : GovernedLoopEffectAttemptTestFixture.Now;
    }
}
