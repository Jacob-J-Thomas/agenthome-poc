using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

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
        Assert.Equal(CommandOperationId(registration.Template.ContentHash), operation.Descriptor.OperationId);
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

    private static string CommandOperationId(string templateContentHash)
        => "command/" + templateContentHash[..32] + "/" + templateContentHash[32..];

    private sealed class StubNativeHost : ICommandActionNativeHost
    {
        internal int PrepareCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        internal CommandActionNativePreparation? Preparation { get; set; }
        internal CommandActionNativeExecutionRequest? LastRequest { get; private set; }
        internal CommandActionNativeOutcome? Outcome { get; set; }
        internal bool PreparationIsCurrent { get; set; } = true;

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
            => Task.FromResult(PreparationIsCurrent);

        public async Task<CommandActionNativeExecutionResult> ExecuteAsync(CommandActionNativeExecutionRequest request, ICommandActionNativeLaunchBoundary launchBoundary, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            LastRequest = request;
            if (Outcome is null)
            {
                return new CommandActionNativeExecutionResult(
                    CommandActionNativeExecutionStatus.DispatchNotStarted,
                    null,
                    CommandActionDispatchNotStartedReason.LaunchAuthorityUnavailable);
            }
            var observed = await launchBoundary.CrossAsync(_ => Task.FromResult(Outcome), cancellationToken);
            return new CommandActionNativeExecutionResult(CommandActionNativeExecutionStatus.OutcomeObserved, observed);
        }

        public Task<CommandActionReconciliationProbeResult> ProbeAsync(CommandActionNativeExecutionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Outcome is null
                ? new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.Indeterminate, null)
                : new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.OutcomeObserved, Outcome));
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
}
