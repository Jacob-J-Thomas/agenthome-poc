using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Tests.LocalWorkspace.Actions;

public sealed class GovernedWorkspaceActionOperationTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(WorkspaceActionKind.Append, WorkspaceActionOperationIds.Append)]
    [InlineData(WorkspaceActionKind.Write, WorkspaceActionOperationIds.Write)]
    [InlineData(WorkspaceActionKind.Delete, WorkspaceActionOperationIds.Delete)]
    public void Descriptor_is_exact_and_requires_canonical_effect_evidence(WorkspaceActionKind kind, string operationId)
    {
        var operation = Operation(kind, new StubNativeHost());

        Assert.Equal(operationId, operation.Descriptor.OperationId);
        Assert.Equal(GovernedActuatorTargetSemantics.ExactWorkspaceTarget, operation.Descriptor.TargetSemantics);
        Assert.Equal(GovernedActuatorIdempotencyPosture.StableOperationIdentity, operation.Descriptor.Idempotency);
        Assert.True(operation.Descriptor.RequiresOptimisticPrecondition);
        Assert.True(operation.Descriptor.RequiresBeforeEvidence);
        Assert.True(operation.Descriptor.RequiresAfterEvidence);
        Assert.True(operation.Descriptor.RequiresOutcomeEvidence);
        Assert.Null(GovernedActuatorOperationContract.Validate(operation.Descriptor));
    }

    [Fact]
    public async Task Preparation_returns_only_exact_server_owned_before_identity()
    {
        var host = new StubNativeHost();
        var operation = Operation(WorkspaceActionKind.Write, host);
        var input = Input(WorkspaceActionKind.Write, "notes/file.txt", "alpha", new WorkspaceActionPrecondition(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null));
        var before = Before(input, WorkspaceActionEntryKind.Absent, null, null, 0, 0);
        host.Preparation = new WorkspaceActionNativePreparation(before);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(WorkspaceActionInputContract.Encode(input), out var generic, out _));

        var preparation = await operation.PrepareAsync(generic!);

        Assert.Equal(before.EvidenceId, preparation?.BeforeEvidenceId);
        Assert.Equal(before.TargetFingerprint, preparation?.TargetFingerprint);
        Assert.Equal(before.PreconditionEvidenceHash, preparation?.PreconditionEvidenceHash);
        Assert.Equal(1, host.PrepareCalls);
    }

    [Fact]
    public async Task Credential_reference_is_unavailable_before_native_preparation_or_intent()
    {
        var host = new StubNativeHost();
        var operation = Operation(WorkspaceActionKind.Write, host);
        var input = Input(
            WorkspaceActionKind.Write,
            "notes/file.txt",
            null,
            new WorkspaceActionPrecondition(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null),
            new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.CredentialReference, null, "credential-alpha"));
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(WorkspaceActionInputContract.Encode(input), out var generic, out _));

        Assert.Null(await operation.PrepareAsync(generic!));
        Assert.Equal(0, host.PrepareCalls);
    }

    [Fact]
    public async Task Preparation_rejects_server_evidence_substitution()
    {
        var host = new StubNativeHost();
        var operation = Operation(WorkspaceActionKind.Write, host);
        var input = Input(WorkspaceActionKind.Write, "notes/file.txt", "alpha", new WorkspaceActionPrecondition(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null));
        host.Preparation = new WorkspaceActionNativePreparation(Before(input, withTarget: "other/file.txt"));
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(WorkspaceActionInputContract.Encode(input), out var generic, out _));

        Assert.Null(await operation.PrepareAsync(generic!));
        Assert.Equal(1, host.PrepareCalls);
    }

    [Fact]
    public async Task Invocation_passes_opaque_bound_before_reference_and_rejects_caller_substitution_shape()
    {
        var host = new StubNativeHost();
        var operation = Operation(WorkspaceActionKind.Write, host);
        var input = Input(WorkspaceActionKind.Write, "notes/file.txt", "alpha", new WorkspaceActionPrecondition(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null));
        var before = Before(input, WorkspaceActionEntryKind.Absent, null, null, 0, 0);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(WorkspaceActionInputContract.Encode(input), out var generic, out _));
        host.ExecutionResult = new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        var exact = Invocation(operation, generic!, before);

        _ = await operation.ExecuteAsync(exact, new StubDispatchBoundary());
        var malformed = await operation.ExecuteAsync(exact with { BeforeEvidenceId = "RAW ABSOLUTE PATH" }, new StubDispatchBoundary());

        Assert.Equal(before.EvidenceId, host.LastExecution?.BeforeEvidenceId);
        Assert.Equal(before.TargetFingerprint, host.LastExecution?.TargetFingerprint);
        Assert.Equal(1, host.ExecuteCalls);
        Assert.Equal(GovernedActuatorAdapterStatus.DispatchNotStarted, malformed.Status);
    }

    [Fact]
    public async Task RecoveryProbeAuthenticatesExactInvocationAndProjectsOnlyConclusiveNativeEvidence()
    {
        var host = new StubNativeHost
        {
            ProbeResult = new WorkspaceActionReconciliationProbeResult(
                WorkspaceActionReconciliationPosture.ProvedOutcomeObserved,
                "after-alpha",
                null,
                "outcome-alpha"),
        };
        var operation = Operation(WorkspaceActionKind.Write, host);
        var input = Input(
            WorkspaceActionKind.Write,
            "notes/file.txt",
            "alpha",
            new WorkspaceActionPrecondition(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null));
        var before = Before(input, WorkspaceActionEntryKind.Absent, null, null, 0, 0);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(WorkspaceActionInputContract.Encode(input), out var generic, out _));
        var probe = Assert.IsAssignableFrom<IGovernedActuatorOutcomeProbe>(operation);

        var exact = await probe.ProbeAsync(Invocation(operation, generic!, before));
        var malformed = await probe.ProbeAsync(Invocation(operation, generic!, before) with { TargetFingerprint = "not-a-hash" });

        Assert.Equal(GovernedActuatorProbePosture.OutcomeObserved, exact.Posture);
        Assert.Equal(GovernedLoopEffectOutcome.Succeeded, exact.Outcome?.Outcome);
        Assert.Equal("outcome-alpha", exact.Outcome?.OutcomeEvidenceId);
        Assert.Equal("after-alpha", exact.Outcome?.AfterEvidenceId);
        Assert.Equal(before.EvidenceId, host.LastProbe?.BeforeEvidenceId);
        Assert.Equal(before.TargetFingerprint, host.LastProbe?.TargetFingerprint);
        Assert.Equal(GovernedActuatorProbePosture.Unavailable, malformed.Posture);
        Assert.Equal(1, host.ProbeCalls);
    }

    private static GovernedWorkspaceActionOperation Operation(WorkspaceActionKind kind, IWorkspaceActionNativeHost host)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/workspace-command", out var id, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var provider, out _));
        return new GovernedWorkspaceActionOperation(
            new CapabilityDescriptorIdentity(id!, version!, hash!),
            new CapabilityImplementationIdentity(provider!, "workspace-command"),
            kind,
            host);
    }

    private static WorkspaceActionInput Input(
        WorkspaceActionKind kind,
        string targetValue,
        string? literal,
        WorkspaceActionPrecondition precondition,
        WorkspaceActionContentSegment? segment = null)
    {
        Assert.True(WorkspaceActionScopeId.TryParse("workspace", out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse(targetValue, out var target, out _));
        var segments = kind == WorkspaceActionKind.Delete
            ? []
            : new[] { segment ?? new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.LiteralUtf8, literal, null) };
        return new WorkspaceActionInput(1, kind, scope!, target!, precondition, segments);
    }

    private static WorkspaceActionBeforeEvidence Before(
        WorkspaceActionInput input,
        WorkspaceActionEntryKind entryKind = WorkspaceActionEntryKind.RegularFile,
        string? nativeIdentity = null,
        string? contentHash = null,
        long byteCount = 5,
        long governedVersion = 1,
        string? withTarget = null)
    {
        var target = input.Target;
        if (withTarget is not null)
        {
            Assert.True(WorkspaceRelativeFileTarget.TryParse(withTarget, out target, out _));
        }
        return WorkspaceActionEvidenceContract.CreateBefore(
            input.ScopeId,
            target!,
            new string('1', 64),
            WorkspaceActionInputContract.ComputePreconditionHash(input.Precondition),
            entryKind,
            WorkspaceActionPermissionOperation.For(input.Kind, entryKind),
            new string('6', 64),
            new string('2', 64),
            new string('3', 64),
            entryKind == WorkspaceActionEntryKind.Absent ? null : nativeIdentity ?? new string('4', 64),
            entryKind == WorkspaceActionEntryKind.Absent ? null : contentHash ?? new string('5', 64),
            byteCount,
            governedVersion,
            _now);
    }

    private static GovernedActuatorInvocation Invocation(
        GovernedWorkspaceActionOperation operation,
        GovernedActuatorInputEvidence input,
        WorkspaceActionBeforeEvidence before)
        => new(operation.Descriptor, "effect-alpha", "operation-alpha", 1, input, before.TargetFingerprint, before.PreconditionEvidenceHash, before.EvidenceId);

    private sealed class StubNativeHost : IWorkspaceActionNativeHost
    {
        public int PrepareCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public int ProbeCalls { get; private set; }
        public WorkspaceActionNativePreparation? Preparation { get; set; }
        public WorkspaceActionNativeExecutionRequest? LastExecution { get; private set; }
        public WorkspaceActionReconciliationProbeRequest? LastProbe { get; private set; }
        public WorkspaceActionNativeCommitResult ExecutionResult { get; set; } = new(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        public WorkspaceActionReconciliationProbeResult ProbeResult { get; set; } = new(WorkspaceActionReconciliationPosture.Indeterminate, null, null);
        public Task<WorkspaceActionNativePreparation?> PrepareAsync(WorkspaceActionInput input, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(Preparation);
        }
        public Task<bool> IsPreparationCurrentAsync(WorkspaceActionInput input, string targetFingerprint, string beforeEvidenceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Preparation is not null
                && string.Equals(Preparation.BeforeEvidence.TargetFingerprint, targetFingerprint, StringComparison.Ordinal)
                && string.Equals(Preparation.BeforeEvidence.EvidenceId, beforeEvidenceId, StringComparison.Ordinal));
        public Task<WorkspaceActionNativeCommitResult> ExecuteAsync(WorkspaceActionNativeExecutionRequest request, IWorkspaceActionNativeDispatchBoundary dispatchBoundary, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            LastExecution = request;
            return Task.FromResult(ExecutionResult);
        }
        public Task<WorkspaceActionReconciliationProbeResult> ProbeAsync(WorkspaceActionReconciliationProbeRequest request, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            LastProbe = request;
            return Task.FromResult(ProbeResult);
        }
    }

    private sealed class StubDispatchBoundary : IGovernedActuatorDispatchBoundary
    {
        public Task<GovernedActuatorExternalOutcome> CrossAsync(Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback, CancellationToken cancellationToken = default)
            => callback(cancellationToken);
    }
}
