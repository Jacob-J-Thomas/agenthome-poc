using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationProbeProcessFixture :
    IGovernedLoopEffectReconciliationAuthorizationSource,
    IGovernedLoopEffectReconciliationInputSource,
    IGovernedLoopEffectReconciliationProbeRegistry,
    IGovernedLoopEffectReconciliationProbe
{
    private readonly string _callbackEvidencePath;
    private readonly GovernedLoopEffectReconciliationCase _case;
    private readonly GovernedLoopEffectAttempt _attempt;
    private readonly GovernedActuatorInputEvidence _input;

    private GovernedLoopEffectReconciliationProbeProcessFixture(
        string callbackEvidencePath,
        GovernedLoopEffectReconciliationCase value,
        GovernedLoopEffectAttempt attempt,
        GovernedActuatorInputEvidence input)
    {
        _callbackEvidencePath = callbackEvidencePath;
        _case = value;
        _attempt = attempt;
        _input = input;
    }

    internal GovernedLoopEffectReconciliationCase Case => _case;

    internal GovernedLoopEffectAttempt Attempt => _attempt;

    internal static GovernedLoopEffectReconciliationProbeProcessFixture Create(
        string workspaceRoot,
        string callbackEvidencePath)
    {
        var (_, _, _, attempt, input) = CreateAttemptStages();
        var workspaceId = CapabilityWorkspaceScopeId.Create(workspaceRoot);
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(workspaceId, 1, 1, attempt);
        var metadata = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "contract-probe-process",
            1,
            attempt.Capability,
            attempt.Implementation,
            attempt.ActuatorOperationId,
            attempt.OperationDescriptorHash,
            "probe-process",
            1,
            Hash('8'),
            string.Empty));
        var openedAtUtc = GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(4);
        var open = GovernedLoopEffectReconciliationContract.Open("case-probe-process", binding, metadata, [], [], openedAtUtc);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "source-probe-process",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            open.ContractMetadata.ContractId,
            open.ContractMetadata.ContractVersion,
            open.ContractMetadata.ContentHash,
            Hash('a'),
            openedAtUtc,
            null,
            string.Empty));
        var value = GovernedLoopEffectReconciliationContract.Create(
            open.CaseId,
            open.CaseVersion,
            open.Binding,
            open.ContractMetadata,
            [source],
            [],
            [],
            null,
            null,
            null,
            [],
            null,
            open.OpenedAtUtc,
            open.UpdatedAtUtc);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(value, attempt).IsValid);
        return new GovernedLoopEffectReconciliationProbeProcessFixture(callbackEvidencePath, value, attempt, input);
    }

    internal static async Task SeedAsync(string workspaceRoot)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        var effectStore = new GovernedLoopEffectAttemptStore(paths);
        var (prepared, authorized, crossed, attempt, _) = CreateAttemptStages();
        var created = await effectStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(prepared.ContentHash, authorized, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(authorized.ContentHash, crossed, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(crossed.ContentHash, attempt, created.Lease!)).Status);
        created.Lease!.Dispose();

        var processFixture = Create(workspaceRoot, string.Empty);
        Assert.Equal(attempt, processFixture.Attempt);
        var store = new GovernedLoopEffectReconciliationCaseStore(effectStore);
        var mutation = new GovernedLoopEffectReconciliationCaseMutationRequest(
            "probe-process-seed",
            Hash("probe-process-seed"),
            "open",
            null,
            null,
            processFixture.Case.Binding,
            processFixture.Case);
        var seeded = await store.CompareExchangeAsync(mutation);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, seeded.Status);
        Assert.Equal(processFixture.Case.ContentHash, seeded.Case?.ContentHash);
    }

    internal GovernedLoopEffectReconciliationService CreateService(
        string workspaceRoot,
        GovernedLoopEffectReconciliationCaseStoreOptions? options = null)
    {
        var effectStore = new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspaceRoot));
        var caseStore = new GovernedLoopEffectReconciliationCaseStore(effectStore, options: options);
        return new GovernedLoopEffectReconciliationService(caseStore, this, this, this, caseStore);
    }

    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(
        GovernedLoopEffectReconciliationAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopEffectReconciliationAuthorizationResult(
            GovernedLoopEffectReconciliationAuthorizationStatus.Ready,
            request.Purpose,
            request.Case,
            request.Binding,
            GovernedLoopEffectAttemptTestFixture.Hash('a')));
    }

    public Task<GovernedLoopEffectReconciliationInputReadResult> ReadAsync(
        GovernedLoopEffectReconciliationInputReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopEffectReconciliationInputReadResult(
            GovernedLoopEffectReconciliationInputReadStatus.Found,
            request.Case,
            request.Binding,
            _attempt,
            GovernedLoopEffectReconciliationApplicationTestFixture.ReviewBlockedFrontier(_case, _attempt),
            _input));
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryPage> ListAsync(
        GovernedLoopEffectReconciliationProbeRegistryListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryPage(
            GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready,
            [_case.ContractMetadata],
            null));
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryReadResult> ReadAsync(
        GovernedLoopEffectReconciliationProbeRegistryReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = Equals(request.Contract, _case.ContractMetadata);
        return Task.FromResult(found
            ? new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, _case.ContractMetadata, this)
            : new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict, _case.ContractMetadata, null));
    }

    public Task<GovernedLoopEffectReconciliationProbeInvocationResult> ProbeAsync(
        GovernedLoopEffectReconciliationProbeInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(_attempt.TargetFingerprint, request.Target.TargetFingerprint);
        Assert.Equal(_attempt.PreconditionEvidenceHash, request.Target.PreconditionEvidenceHash);
        Assert.Equal(_attempt.BeforeEvidenceId, request.Target.BeforeEvidenceId);
        AppendCallbackEvidence(request.ProbeInvocationId);
        var observedAt = DateTimeOffset.UtcNow;
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            request.Case.CaseId,
            request.Case.BindingHash,
            "external-probe-observation",
            request.SourceId,
            request.SourceRegistrationHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            request.SourceReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "external-probe-evidence",
            GovernedLoopEffectAttemptTestFixture.Hash('e'),
            observedAt,
            observedAt,
            "The independent probe found no matching external effect.",
            string.Empty));
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeInvocationResult(
            GovernedLoopEffectReconciliationProbeInvocationStatus.Ready,
            observation));
    }

    private void AppendCallbackEvidence(string probeInvocationId)
    {
        var evidencePath = $"{_callbackEvidencePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.callback";
        using var stream = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var evidence = Encoding.UTF8.GetBytes(probeInvocationId + Environment.NewLine);
        stream.Write(evidence);
        stream.Flush(flushToDisk: true);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hash(char value) => new(value, 64);

    private static (GovernedLoopEffectAttempt Prepared, GovernedLoopEffectAttempt Authorized, GovernedLoopEffectAttempt Crossed, GovernedLoopEffectAttempt Attempt, GovernedActuatorInputEvidence Input) CreateAttemptStages()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out var error), error);
        var prepared = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, GovernedLoopEffectAttemptTestFixture.Hash('f'), GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(2));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(3));
        return (prepared, authorized, crossed, attempt, input!);
    }
}
