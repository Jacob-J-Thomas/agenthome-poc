using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationCommandProbe : IGovernedLoopEffectReconciliationProbe
{
    private const string CommandOutcomePrefix = "command-outcome-";
    private const string WorkspaceOutcomePrefix = "outcome-";
    private readonly IGovernedLoopEffectReconciliationCaseStore _cases;
    private readonly GovernedLoopEffectReconciliationContractMetadata _contract;
    private readonly GovernedActuatorOperationDescriptor _descriptor;
    private readonly IGovernedLoopEffectReconciliationInputSource _inputs;
    private readonly IGovernedActuatorOutcomeProbe _probe;
    private readonly TimeProvider _timeProvider;

    internal GovernedLoopEffectReconciliationCommandProbe(
        GovernedLoopEffectReconciliationContractMetadata contract,
        GovernedActuatorOperationDescriptor descriptor,
        IGovernedActuatorOutcomeProbe probe,
        IGovernedLoopEffectReconciliationCaseStore cases,
        IGovernedLoopEffectReconciliationInputSource inputs,
        TimeProvider timeProvider)
    {
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _cases = cases ?? throw new ArgumentNullException(nameof(cases));
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GovernedLoopEffectReconciliationProbeInvocationResult> ProbeAsync(GovernedLoopEffectReconciliationProbeInvocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        GovernedLoopEffectReconciliationCaseReadResult current;
        try
        {
            current = await _cases.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(request.Case), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }
        if (current.Status == GovernedLoopEffectReconciliationCaseReadStatus.NotFound)
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound);
        }
        if (current.Status != GovernedLoopEffectReconciliationCaseReadStatus.Found || current.Case is null)
        {
            return Closed(current.Status == GovernedLoopEffectReconciliationCaseReadStatus.Corrupt
                ? GovernedLoopEffectReconciliationProbeInvocationStatus.Corrupt
                : GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }

        var reconciliationCase = current.Case;
        var sources = reconciliationCase.EvidenceSources.Where(source => string.Equals(source.SourceId, request.SourceId, StringComparison.Ordinal)
            && string.Equals(source.ContentHash, request.SourceRegistrationHash, StringComparison.Ordinal)
            && source.ReliabilityPosture == request.SourceReliabilityPosture).Take(2).ToArray();
        if (!Equals(reconciliationCase.ContractMetadata, _contract)
            || sources.Length != 1
            || !string.Equals(request.Case.BindingHash, reconciliationCase.Binding.ContentHash, StringComparison.Ordinal))
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Invalid);
        }

        GovernedLoopEffectReconciliationInputReadResult input;
        try
        {
            input = await _inputs.ReadAsync(new GovernedLoopEffectReconciliationInputReadRequest(request.Case, reconciliationCase.Binding), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }
        if (input.Status != GovernedLoopEffectReconciliationInputReadStatus.Found || input.EffectHead is null || input.Input is null)
        {
            return Closed(input.Status == GovernedLoopEffectReconciliationInputReadStatus.NotFound
                ? GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound
                : input.Status is GovernedLoopEffectReconciliationInputReadStatus.Corrupt or GovernedLoopEffectReconciliationInputReadStatus.Conflict
                    ? GovernedLoopEffectReconciliationProbeInvocationStatus.Corrupt
                    : GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }

        var effect = input.EffectHead;
        if (!string.Equals(effect.TargetFingerprint, request.Target.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(effect.PreconditionEvidenceHash, request.Target.PreconditionEvidenceHash, StringComparison.Ordinal)
            || !string.Equals(effect.BeforeEvidenceId, request.Target.BeforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(effect.ActuatorOperationId, _descriptor.OperationId, StringComparison.Ordinal)
            || !string.Equals(effect.OperationDescriptorHash, _descriptor.ContentHash, StringComparison.Ordinal)
            || !Equals(effect.Capability, _descriptor.Capability)
            || !Equals(effect.Implementation, _descriptor.Implementation))
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Corrupt);
        }

        GovernedActuatorProbeResult observed;
        try
        {
            observed = await _probe.ProbeAsync(new GovernedActuatorInvocation(
                _descriptor,
                effect.Payload.EffectId,
                effect.Payload.OperationId,
                effect.Payload.EffectGeneration,
                input.Input,
                effect.TargetFingerprint,
                effect.PreconditionEvidenceHash,
                effect.BeforeEvidenceId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }

        if (observed.Posture != GovernedActuatorProbePosture.OutcomeObserved || observed.Outcome is null)
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }
        if (!TryReadEvidenceHash(observed.Outcome.OutcomeEvidenceId, out var evidenceHash)
            || observed.Outcome.Outcome is not (GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed))
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Corrupt);
        }

        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable);
        }
        var observationId = "observation-" + Hash(request.ProbeInvocationId, request.SourceRegistrationHash, observed.Outcome.OutcomeEvidenceId);
        try
        {
            var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                request.Case.CaseId,
                request.Case.BindingHash,
                observationId,
                request.SourceId,
                request.SourceRegistrationHash,
                GovernedLoopEffectReconciliationObservationKind.Evidence,
                request.SourceReliabilityPosture,
                observed.Outcome.Outcome == GovernedLoopEffectOutcome.Succeeded
                    ? GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded
                    : GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed,
                observed.Outcome.OutcomeEvidenceId,
                evidenceHash,
                now,
                now,
                null,
                string.Empty));
            return new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation);
        }
        catch (ArgumentException)
        {
            return Closed(GovernedLoopEffectReconciliationProbeInvocationStatus.Corrupt);
        }
    }

    private static bool TryReadEvidenceHash(string? evidenceId, out string? evidenceHash)
    {
        evidenceHash = null;
        var prefix = evidenceId?.StartsWith(CommandOutcomePrefix, StringComparison.Ordinal) == true
            ? CommandOutcomePrefix
            : evidenceId?.StartsWith(WorkspaceOutcomePrefix, StringComparison.Ordinal) == true
                ? WorkspaceOutcomePrefix
                : null;
        if (prefix is null || evidenceId!.Length != prefix.Length + GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters)
        {
            return false;
        }
        var candidate = evidenceId[prefix.Length..];
        if (candidate.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return false;
        }
        evidenceHash = candidate;
        return true;
    }

    private static string Hash(params string[] values)
    {
        var builder = new StringBuilder("embodysense.actuator-reconciliation-observation.v1\n");
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static GovernedLoopEffectReconciliationProbeInvocationResult Closed(GovernedLoopEffectReconciliationProbeInvocationStatus status)
        => new(status, null);
}
