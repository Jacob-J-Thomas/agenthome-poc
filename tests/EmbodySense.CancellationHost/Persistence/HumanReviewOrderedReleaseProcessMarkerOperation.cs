using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessMarkerOperation(
    string markerPath,
    bool crashAfterMarker,
    string? ownerReadyPath = null,
    string? ownerReleasePath = null) : IGovernedActuatorOperation, IGovernedActuatorOutcomeProbe, IGovernedActuatorPreparationValidator
{
    private const int ResponseLossExitCode = 181;
    private readonly string _markerPath = Path.GetFullPath(markerPath);
    private readonly string? _ownerReadyPath = ownerReadyPath is null ? null : Path.GetFullPath(ownerReadyPath);
    private readonly string? _ownerReleasePath = ownerReleasePath is null ? null : Path.GetFullPath(ownerReleasePath);

    public GovernedActuatorOperationDescriptor Descriptor { get; } = CreateDescriptor();

    public string? ValidateInput(GovernedActuatorInputEvidence input)
        => WorkspaceActionInputContract.TryParse(input.CanonicalJson, WorkspaceActionKind.Write, out _, out var reason) ? null : reason ?? "marker-input-invalid";

    public Task<GovernedActuatorPreparationEvidence?> PrepareAsync(GovernedActuatorInputEvidence input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GovernedActuatorPreparationEvidence?>(File.Exists(_markerPath) ? null : Preparation());
    }

    public Task<bool> IsPreparationCurrentAsync(GovernedActuatorInputEvidence input, GovernedActuatorPreparationEvidence preparation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!File.Exists(_markerPath) && Equals(preparation, Preparation()));
    }

    public async Task<GovernedActuatorAdapterResult> ExecuteAsync(GovernedActuatorInvocation invocation, IGovernedActuatorDispatchBoundary dispatchBoundary, CancellationToken cancellationToken = default)
    {
        await WaitAtOwnedEffectBarrierAsync(cancellationToken);
        var outcome = await dispatchBoundary.CrossAsync(token => CreateMarkerAsync(invocation.IdempotencyOperationId, token), cancellationToken);
        return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
    }

    public async Task<GovernedActuatorProbeResult> ProbeAsync(GovernedActuatorInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_markerPath))
        {
            return new GovernedActuatorProbeResult(GovernedActuatorProbePosture.ProvedNotStarted, null);
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(_markerPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null);
        }
        return string.Equals(content, invocation.IdempotencyOperationId + Environment.NewLine, StringComparison.Ordinal)
            ? new GovernedActuatorProbeResult(GovernedActuatorProbePosture.OutcomeObserved, Outcome(invocation.IdempotencyOperationId))
            : new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null);
    }

    private async Task<GovernedActuatorExternalOutcome> CreateMarkerAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var content = Encoding.UTF8.GetBytes(operationId + Environment.NewLine);
            await stream.WriteAsync(content, cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(_markerPath) && string.Equals(File.ReadAllText(_markerPath), operationId + Environment.NewLine, StringComparison.Ordinal))
        {
        }

        if (crashAfterMarker)
        {
            Console.Error.WriteLine("The test host process lost the actuator response after creating the stable observable marker.");
            Console.Error.Flush();
            Environment.Exit(ResponseLossExitCode);
            throw new InvalidOperationException("The response-loss process did not terminate.");
        }
        return Outcome(operationId);
    }

    private static GovernedActuatorExternalOutcome Outcome(string operationId)
    {
        var identity = Hash(operationId)[..24];
        return new GovernedActuatorExternalOutcome(GovernedLoopEffectOutcome.Succeeded, "marker-outcome-" + identity, "marker-after-" + identity);
    }

    private static GovernedActuatorOperationDescriptor CreateDescriptor()
    {
        var capability = HumanReviewOrderedReleaseGraphFixture.WorkspaceCapability();
        _ = CapabilityDescriptorIdentity.TryCreate(capability, out var identity, out _);
        return GovernedActuatorOperationContract.Create(
            1,
            identity!,
            capability.Implementation,
            WorkspaceActionOperationIds.For(WorkspaceActionKind.Write),
            "Create one stable process-observable marker after governed Human Review.",
            GovernedActuatorTargetSemantics.ExactWorkspaceTarget,
            GovernedActuatorIdempotencyPosture.StableOperationIdentity,
            requiresOptimisticPrecondition: true,
            GovernedActuatorApprovalPosture.GovernedApprovalRequired,
            unattendedEligible: false,
            GovernedActuatorCancellationPosture.BeforeBoundaryOnly,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBeforeEvidence: true,
            requiresAfterEvidence: true,
            requiresOutcomeEvidence: true);
    }

    private GovernedActuatorPreparationEvidence Preparation()
    {
        var targetFingerprint = Hash(_markerPath);
        return new GovernedActuatorPreparationEvidence(targetFingerprint, Hash("marker-absent:" + targetFingerprint), "marker-before-" + targetFingerprint[..24]);
    }

    private async Task WaitAtOwnedEffectBarrierAsync(CancellationToken cancellationToken)
    {
        if (_ownerReadyPath is null || _ownerReleasePath is null) return;
        await File.WriteAllTextAsync(_ownerReadyPath, "ready", cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(_ownerReleasePath)) await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
