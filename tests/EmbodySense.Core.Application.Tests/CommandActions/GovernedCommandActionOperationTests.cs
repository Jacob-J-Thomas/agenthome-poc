using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Tests.CommandActions;

public sealed class GovernedCommandActionOperationTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registration_and_descriptor_pin_one_exact_template_hash_reconciliation_only_actuator()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var operation = new GovernedCommandActionOperation(registration, new StubNativeHost());

        Assert.Null(CommandActionRegistrationContract.Validate(registration));
        var expectedOperationId = "command/" + registration.Template.ContentHash[..32] + "/" + registration.Template.ContentHash[32..];
        Assert.Equal(expectedOperationId, GovernedCommandActionOperation.CreateOperationId(registration.Template));
        Assert.Equal(expectedOperationId, operation.Descriptor.OperationId);
        var registry = new GovernedActuatorOperationRegistry([operation]);
        Assert.True(registry.TryResolve(operation.Descriptor, out var resolved));
        Assert.Same(operation, resolved);
        Assert.Equal(GovernedActuatorTargetSemantics.ExactOpaqueFingerprint, operation.Descriptor.TargetSemantics);
        Assert.Equal(GovernedActuatorIdempotencyPosture.ReconciliationOnly, operation.Descriptor.Idempotency);
        Assert.True(operation.Descriptor.RequiresOptimisticPrecondition);
        Assert.True(operation.Descriptor.RequiresBeforeEvidence);
        Assert.True(operation.Descriptor.RequiresOutcomeEvidence);
        Assert.False(operation.Descriptor.RequiresAfterEvidence);
        Assert.Null(EmbodySense.Core.Common.Loops.Execution.Effects.GovernedActuatorOperationContract.Validate(operation.Descriptor));
        Assert.NotNull(CommandActionRegistrationContract.Validate(registration with
        {
            Manifest = registration.Manifest with { Checksum = EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute("other"u8) },
        }));
    }

    [Fact]
    public void Registration_validation_rejects_missing_resource_and_access_policy_bindings()
    {
        var registration = CommandActionApplicationTestData.Registration();

        Assert.Equal("command-registration-required", CommandActionRegistrationContract.Validate(null));

        var resourceConflict = registration with
        {
            Template = RecreateTemplate(registration, registration.Template.Isolation with { MaxOutputBytes = registration.Template.Isolation.MaxOutputBytes + 1 }),
        };
        Assert.Equal("command-registration-resource-policy-conflict", CommandActionRegistrationContract.Validate(resourceConflict));

        var accessConflict = registration with { Template = RecreateTemplate(registration, requiresCredentialChannel: true) };
        Assert.Equal("command-registration-access-policy-conflict", CommandActionRegistrationContract.Validate(accessConflict));
    }

    [Fact]
    public void Operation_constructor_rejects_a_registration_without_an_artifact_manifest()
    {
        var registration = CommandActionApplicationTestData.Registration() with { Manifest = null! };

        Assert.Throws<ArgumentException>(() => new GovernedCommandActionOperation(registration, new StubNativeHost()));
    }

    [Fact]
    public void Graph_projection_rejects_a_missing_registration_without_assigning_payload()
    {
        Assert.False(CommandActionGraphProjectionContract.TryGetPayloadCharacters(null, out var payloadCharacters));
        Assert.Equal(0, payloadCharacters);
    }

    [Fact]
    public async Task Preparation_returns_only_coherent_server_owned_evidence()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var input = CommandActionApplicationTestData.Input(registration);
        Assert.True(CommandActionInputContract.TryMaterialize(input.CanonicalJson, registration.Template, out var materialized, out _));
        var evidence = CommandActionEvidenceContract.CreatePreparation(
            registration.Template, materialized!.InputFingerprint, new string('1', 64), new string('2', 64), _now);
        var host = new StubNativeHost { Preparation = new CommandActionNativePreparation(evidence) };
        var operation = new GovernedCommandActionOperation(registration, host);

        var prepared = await operation.PrepareAsync(input);
        host.Preparation = new CommandActionNativePreparation(evidence with { InputFingerprint = new string('3', 64) });
        var substituted = await operation.PrepareAsync(input);

        Assert.Equal(evidence.EvidenceId, prepared?.BeforeEvidenceId);
        Assert.Equal(evidence.TargetFingerprint, prepared?.TargetFingerprint);
        Assert.Equal(evidence.PreconditionEvidenceHash, prepared?.PreconditionEvidenceHash);
        Assert.Null(substituted);
        Assert.Equal(2, host.PrepareCalls);
    }

    [Fact]
    public async Task Credential_template_remains_unavailable_without_calling_native_host()
    {
        var registration = CommandActionApplicationTestData.Registration(credentials: true);
        var host = new StubNativeHost();
        var operation = new GovernedCommandActionOperation(registration, host);

        Assert.Null(await operation.PrepareAsync(CommandActionApplicationTestData.Input(registration)));
        Assert.Equal(0, host.PrepareCalls);
    }

    [Fact]
    public void Input_validation_returns_the_closed_parser_reason_and_rejects_null()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var operation = new GovernedCommandActionOperation(registration, new StubNativeHost());
        var input = CommandActionApplicationTestData.Input(registration);

        Assert.Null(operation.ValidateInput(input));
        Assert.NotNull(operation.ValidateInput(input with { CanonicalJson = "{}" }));
        Assert.Throws<ArgumentNullException>(() => operation.ValidateInput(null!));
    }

    [Fact]
    public async Task Preparation_current_forwards_the_exact_preparation_evidence_to_the_native_host()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var input = CommandActionApplicationTestData.Input(registration);
        var host = new StubNativeHost();
        var operation = new GovernedCommandActionOperation(registration, host);
        var preparation = new GovernedActuatorPreparationEvidence(
            new string('1', 64),
            new string('2', 64),
            "command-before-" + new string('3', 64));

        Assert.True(await operation.IsPreparationCurrentAsync(input, preparation));
        Assert.Same(input, host.LastCurrentInput);
        Assert.Equal(preparation.TargetFingerprint, host.LastTargetFingerprint);
        Assert.Equal(preparation.PreconditionEvidenceHash, host.LastPreconditionEvidenceHash);
        Assert.Equal(preparation.BeforeEvidenceId, host.LastBeforeEvidenceId);
    }

    [Fact]
    public async Task Execute_forwards_exact_bound_request_and_rejects_forged_before_reference()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost();
        var operation = new GovernedCommandActionOperation(registration, host);
        var input = CommandActionApplicationTestData.Input(registration);
        var invocation = new GovernedActuatorInvocation(
            operation.Descriptor, "effect-alpha", "operation-alpha", 1, input,
            new string('1', 64), new string('2', 64), "command-before-" + new string('3', 64));

        var exact = await operation.ExecuteAsync(invocation, new ImmediateBoundary());
        var forged = await operation.ExecuteAsync(invocation with { BeforeEvidenceId = "raw path" }, new ImmediateBoundary());

        Assert.Equal(GovernedActuatorAdapterStatus.DispatchNotStarted, exact.Status);
        Assert.Equal(GovernedActuatorDispatchNotStartedReason.LaunchAuthorityUnavailable, exact.DispatchNotStartedReason);
        Assert.Equal(invocation.BeforeEvidenceId, host.LastRequest?.BeforeEvidenceId);
        Assert.Equal(1, host.ExecuteCalls);
        Assert.Equal(GovernedActuatorAdapterStatus.DispatchNotStarted, forged.Status);
        Assert.Equal(GovernedActuatorDispatchNotStartedReason.InvalidRequest, forged.DispatchNotStartedReason);
    }

    [Theory]
    [InlineData(CommandActionDispatchNotStartedReason.InvalidRequest, GovernedActuatorDispatchNotStartedReason.InvalidRequest)]
    [InlineData(CommandActionDispatchNotStartedReason.PreparationUnavailable, GovernedActuatorDispatchNotStartedReason.PreparationUnavailable)]
    [InlineData(CommandActionDispatchNotStartedReason.ArtifactUnavailable, GovernedActuatorDispatchNotStartedReason.ArtifactUnavailable)]
    [InlineData(CommandActionDispatchNotStartedReason.ConcurrencyUnavailable, GovernedActuatorDispatchNotStartedReason.ConcurrencyUnavailable)]
    public async Task Execute_maps_each_closed_native_pre_dispatch_reason(CommandActionDispatchNotStartedReason nativeReason, GovernedActuatorDispatchNotStartedReason expectedReason)
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost { DispatchNotStartedReason = nativeReason };
        var operation = new GovernedCommandActionOperation(registration, host);

        var result = await operation.ExecuteAsync(Invocation(operation, registration), new ImmediateBoundary());

        Assert.Equal(GovernedActuatorAdapterStatus.DispatchNotStarted, result.Status);
        Assert.Equal(expectedReason, result.DispatchNotStartedReason);
    }

    [Fact]
    public async Task Execute_rejects_an_unknown_native_pre_dispatch_reason()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost { DispatchNotStartedReason = (CommandActionDispatchNotStartedReason)999 };
        var operation = new GovernedCommandActionOperation(registration, host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(Invocation(operation, registration), new ImmediateBoundary()));
    }

    [Fact]
    public async Task Execute_rejects_an_incoherent_native_execution_result()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost { ReturnIncoherentExecutionResult = true };
        var operation = new GovernedCommandActionOperation(registration, host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(Invocation(operation, registration), new ImmediateBoundary()));
    }

    [Theory]
    [InlineData(CommandActionNativeOutcomeKind.Succeeded, EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(CommandActionNativeOutcomeKind.Failed, EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Failed)]
    public async Task Native_outcome_crosses_and_matches_the_canonical_effect_boundary(
        CommandActionNativeOutcomeKind nativeKind,
        EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome expected)
    {
        var registration = CommandActionApplicationTestData.Registration();
        var evidenceId = "command-outcome-" + new string('4', 64);
        var host = new StubNativeHost { Outcome = new CommandActionNativeOutcome(nativeKind, evidenceId) };
        var operation = new GovernedCommandActionOperation(registration, host);
        var input = CommandActionApplicationTestData.Input(registration);
        var invocation = new GovernedActuatorInvocation(
            operation.Descriptor, "effect-alpha", "operation-alpha", 1, input,
            new string('1', 64), new string('2', 64), "command-before-" + new string('3', 64));
        var boundary = new CountingBoundary();

        var result = await operation.ExecuteAsync(invocation, boundary);

        Assert.Equal(1, boundary.Calls);
        Assert.Equal(GovernedActuatorAdapterStatus.OutcomeObserved, result.Status);
        Assert.Equal(expected, result.Outcome?.Outcome);
        Assert.Equal(evidenceId, result.Outcome?.OutcomeEvidenceId);
    }

    [Fact]
    public async Task Probe_rejects_null_and_invalid_invocations_before_calling_the_native_host()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost();
        var operation = new GovernedCommandActionOperation(registration, host);
        var invocation = Invocation(operation, registration);

        await Assert.ThrowsAsync<ArgumentNullException>(() => operation.ProbeAsync(null!));
        var result = await operation.ProbeAsync(invocation with { BeforeEvidenceId = "raw path" });

        Assert.Equal(GovernedActuatorProbePosture.Unavailable, result.Posture);
        Assert.Null(result.Outcome);
        Assert.Equal(0, host.ProbeCalls);
    }

    [Theory]
    [InlineData(CommandActionNativeOutcomeKind.Succeeded, GovernedLoopEffectOutcome.Succeeded)]
    [InlineData(CommandActionNativeOutcomeKind.Failed, GovernedLoopEffectOutcome.Failed)]
    public async Task Probe_projects_each_observed_native_outcome(CommandActionNativeOutcomeKind nativeKind, GovernedLoopEffectOutcome expectedOutcome)
    {
        var registration = CommandActionApplicationTestData.Registration();
        var evidenceId = "command-probe-" + new string('5', 64);
        var host = new StubNativeHost { Outcome = new CommandActionNativeOutcome(nativeKind, evidenceId) };
        var operation = new GovernedCommandActionOperation(registration, host);

        var result = await operation.ProbeAsync(Invocation(operation, registration));

        Assert.Equal(GovernedActuatorProbePosture.OutcomeObserved, result.Posture);
        Assert.Equal(expectedOutcome, result.Outcome?.Outcome);
        Assert.Equal(evidenceId, result.Outcome?.OutcomeEvidenceId);
        Assert.Equal(1, host.ProbeCalls);
    }

    [Fact]
    public async Task Probe_preserves_indeterminate_posture_without_an_outcome()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost();
        var operation = new GovernedCommandActionOperation(registration, host);

        var result = await operation.ProbeAsync(Invocation(operation, registration));

        Assert.Equal(GovernedActuatorProbePosture.Indeterminate, result.Posture);
        Assert.Null(result.Outcome);
        Assert.Equal(1, host.ProbeCalls);
    }

    [Fact]
    public async Task Execute_rejects_incomplete_native_outcomes_at_the_launch_boundary()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost
        {
            Outcome = new CommandActionNativeOutcome(CommandActionNativeOutcomeKind.Succeeded, "command-outcome-" + new string('6', 64)),
            ReturnIncompleteOutcome = true,
        };
        var operation = new GovernedCommandActionOperation(registration, host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(Invocation(operation, registration), new ImmediateBoundary()));
    }

    [Fact]
    public async Task Execute_rejects_boundary_evidence_that_conflicts_with_the_native_outcome()
    {
        var registration = CommandActionApplicationTestData.Registration();
        var host = new StubNativeHost { Outcome = new CommandActionNativeOutcome(CommandActionNativeOutcomeKind.Succeeded, "command-outcome-" + new string('7', 64)) };
        var operation = new GovernedCommandActionOperation(registration, host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(Invocation(operation, registration), new MismatchingBoundary()));
    }

    private static GovernedActuatorInvocation Invocation(GovernedCommandActionOperation operation, CommandActionRegistration registration)
        => new(
            operation.Descriptor,
            "effect-alpha",
            "operation-alpha",
            1,
            CommandActionApplicationTestData.Input(registration),
            new string('1', 64),
            new string('2', 64),
            "command-before-" + new string('3', 64));

    private static CommandActionTemplate RecreateTemplate(CommandActionRegistration registration, CommandActionIsolationPolicy? isolation = null, bool? requiresCredentialChannel = null)
    {
        var template = registration.Template;
        return CommandActionTemplateContract.Create(
            template.SchemaVersion,
            template.Capability,
            template.Implementation,
            template.ArtifactDigest,
            template.ActivationRevision,
            template.TemplateId,
            template.TemplateVersion,
            template.Slots,
            template.Arguments,
            template.Environment,
            template.SecondaryGrammar,
            template.StandardInput,
            template.StandardInputSlot,
            template.Output,
            isolation ?? template.Isolation,
            requiresCredentialChannel ?? template.RequiresCredentialChannel);
    }

    private sealed class StubNativeHost : ICommandActionNativeHost
    {
        internal int PrepareCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        internal int ProbeCalls { get; private set; }
        internal CommandActionNativePreparation? Preparation { get; set; }
        internal CommandActionNativeExecutionRequest? LastRequest { get; private set; }
        internal CommandActionNativeOutcome? Outcome { get; set; }
        internal CommandActionDispatchNotStartedReason DispatchNotStartedReason { get; set; } = CommandActionDispatchNotStartedReason.LaunchAuthorityUnavailable;
        internal bool ReturnIncoherentExecutionResult { get; set; }
        internal bool ReturnIncompleteOutcome { get; set; }
        internal bool PreparationIsCurrent { get; set; } = true;
        internal GovernedActuatorInputEvidence? LastCurrentInput { get; private set; }
        internal string? LastTargetFingerprint { get; private set; }
        internal string? LastPreconditionEvidenceHash { get; private set; }
        internal string? LastBeforeEvidenceId { get; private set; }

        public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration)
            => new(CapabilityExecutableAvailabilityStatus.Available, "available");

        public Task<CapabilityExecutableAvailability> CheckExecutableAvailabilityAsync(CommandActionRegistration registration, CancellationToken cancellationToken = default)
            => Task.FromResult(CheckAvailability(registration));

        public Task<CommandActionNativePreparation?> PrepareAsync(CommandActionRegistration registration, EmbodySense.Core.Common.Loops.Execution.Effects.Models.GovernedActuatorInputEvidence input, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(Preparation);
        }

        public Task<bool> IsPreparationCurrentAsync(
            CommandActionRegistration registration,
            GovernedActuatorInputEvidence input,
            string targetFingerprint,
            string preconditionEvidenceHash,
            string beforeEvidenceId,
            CancellationToken cancellationToken = default)
        {
            LastCurrentInput = input;
            LastTargetFingerprint = targetFingerprint;
            LastPreconditionEvidenceHash = preconditionEvidenceHash;
            LastBeforeEvidenceId = beforeEvidenceId;
            return Task.FromResult(PreparationIsCurrent);
        }

        public async Task<CommandActionNativeExecutionResult> ExecuteAsync(CommandActionNativeExecutionRequest request, ICommandActionNativeLaunchBoundary launchBoundary, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            LastRequest = request;
            if (ReturnIncoherentExecutionResult)
            {
                return new CommandActionNativeExecutionResult(CommandActionNativeExecutionStatus.OutcomeObserved, null);
            }
            if (Outcome is null)
            {
                return new CommandActionNativeExecutionResult(
                    CommandActionNativeExecutionStatus.DispatchNotStarted,
                    null,
                    DispatchNotStartedReason);
            }
            var observed = await launchBoundary.CrossAsync(_ => ReturnIncompleteOutcome ? Task.FromResult<CommandActionNativeOutcome>(null!) : Task.FromResult(Outcome), cancellationToken);
            return new CommandActionNativeExecutionResult(CommandActionNativeExecutionStatus.OutcomeObserved, observed);
        }

        public Task<CommandActionReconciliationProbeResult> ProbeAsync(CommandActionNativeExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(Outcome is null
                ? new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.Indeterminate, null)
                : new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.OutcomeObserved, Outcome));
        }
    }

    private sealed class ImmediateBoundary : IGovernedActuatorDispatchBoundary
    {
        public Task<GovernedActuatorExternalOutcome> CrossAsync(Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback, CancellationToken cancellationToken = default)
            => callback(cancellationToken);
    }

    private sealed class CountingBoundary : IGovernedActuatorDispatchBoundary
    {
        internal int Calls { get; private set; }

        public Task<GovernedActuatorExternalOutcome> CrossAsync(Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback, CancellationToken cancellationToken = default)
        {
            Calls++;
            return callback(cancellationToken);
        }
    }

    private sealed class MismatchingBoundary : IGovernedActuatorDispatchBoundary
    {
        public async Task<GovernedActuatorExternalOutcome> CrossAsync(Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback, CancellationToken cancellationToken = default)
        {
            await callback(cancellationToken);
            return new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "command-mismatch-" + new string('8', 64), null);
        }
    }
}
