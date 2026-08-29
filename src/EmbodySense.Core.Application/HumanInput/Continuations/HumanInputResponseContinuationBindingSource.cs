using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.HumanInput.Continuations;

/// <summary>Rehydrates one exact selected Human Input value for ordered execution without copying it into the durable continuation trace.</summary>
public sealed class HumanInputResponseContinuationBindingSource : IGovernedLoopSequentialHumanInputBindingSource
{
    private readonly IHumanInputResponseLifecycleStore _responses;

    /// <summary>Creates the source over the one authoritative response lifecycle store.</summary>
    /// <param name="responses">The authenticated response lifecycle store that owns response values and selection state.</param>
    public HumanInputResponseContinuationBindingSource(IHumanInputResponseLifecycleStore responses)
    {
        _responses = responses ?? throw new ArgumentNullException(nameof(responses));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopSequentialHumanInputBindingReadResult> ResolveAsync(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!TryGetTerminalSelection(checkpoint, out var selection) || selection is null)
        {
            return Invalid();
        }

        var request = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        HumanInputResponseLifecycleStoreReadResult read;
        try
        {
            read = await _responses.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }

        if (read is null
            || !Enum.IsDefined(read.Status))
        {
            return Invalid();
        }
        if (read.Status is HumanInputResponseLifecycleStoreReadStatus.Unavailable or HumanInputResponseLifecycleStoreReadStatus.Ambiguous)
        {
            return Unavailable();
        }
        if (read.Status != HumanInputResponseLifecycleStoreReadStatus.Ready || read.Snapshot is null)
        {
            return Invalid();
        }
        try
        {
            if (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(read.Snapshot, request, out var snapshot)
                || snapshot is null
                || snapshot.Request.Head.Status != HumanInputRequestLifecycleStatus.Answered
                || !Equals(snapshot.Request.Head.CurrentRequest, request)
                || !Equals(snapshot.Request.Head.AnswerSelection, selection)
                || snapshot.Selection is null
                || !Equals(HumanInputResponseSelectionReference.Create(snapshot.Selection), selection)
                || !HumanInputResponseLifecycleStoreSnapshotGuard.TryGetActiveResponses(snapshot, out var active)
                || active is null
                || !TryProjectSelectedValue(checkpoint, selection, snapshot.Selection, active, out var response, out var value)
                || response is null
                || value is null)
            {
                return Invalid();
            }

            return new GovernedLoopSequentialHumanInputBindingReadResult(
                GovernedLoopSequentialHumanInputBindingReadStatus.Ready,
                new GovernedLoopSequentialHumanInputBinding(
                    GovernedLoopSequentialHumanInputBinding.CurrentSchemaVersion,
                    checkpoint.Binding.CheckpointId,
                    snapshot.Selection,
                    response,
                    value));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Invalid();
        }
    }

    private static bool TryGetTerminalSelection(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        out HumanInputResponseSelectionReference? selection)
    {
        selection = null;
        return GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint).IsValid
            && checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            && checkpoint.Evidence is
        [
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published },
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, AnswerSelection: not null } answered,
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized, TerminalizationReceiptHash: not null },
        ]
            && (selection = answered.AnswerSelection) is not null
            && HumanInputResponseContractValidator.ValidateSelectionReference(selection).IsValid;
    }

    private static bool TryProjectSelectedValue(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputResponseSelectionReference selectionReference,
        HumanInputResponseSelection selection,
        IReadOnlyList<HumanInputResponseArtifact> active,
        out HumanInputResponseReference? response,
        out GovernedLoopTypedValue? value)
    {
        // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/351): Generic downstream dataflow artifact and handling propagation remain owned by #351; this continuation seam keeps its own checkpoint, wake, claim, and audit evidence value-free.
        response = null;
        value = null;
        if (!Equals(selectionReference, HumanInputResponseSelectionReference.Create(selection))
            || selection.Responses.IsDefaultOrEmpty)
        {
            return false;
        }

        var selected = new HumanInputResponseArtifact[selection.Responses.Length];
        for (var index = 0; index < selection.Responses.Length; index++)
        {
            var matches = active.Where(candidate => selection.Responses[index].Matches(checkpoint.Request, candidate)).Take(2).ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            selected[index] = matches[0];
        }

        var projected = selection.PolicyKind switch
        {
            HumanInputResponsePolicyKind.FirstValid or HumanInputResponsePolicyKind.ManualSelection when selected.Length == 1 => selected[0],
            HumanInputResponsePolicyKind.Quorum when HasExactQuorumValue(checkpoint.Request.ResponseSchema, selected) => selected[0],
            _ => null,
        };
        if (projected is null
            || !HumanInputResponseReference.TryCreate(checkpoint.Request, projected, out response, out _)
            || response is null
            || !Equals(response, selection.Responses[0])
            || !HumanInputResponseValueProjector.TryProject(checkpoint.Request.ResponseSchema, projected.Value, out value)
            || value is null)
        {
            return false;
        }

        return true;
    }

    private static bool HasExactQuorumValue(HumanInputResponseSchema schema, IReadOnlyList<HumanInputResponseArtifact> selected)
    {
        if (selected.Count < 2
            || selected[0] is null
            || !HumanInputResponseValueHash.Matches(selected[0].Value, selected[0].ValueHash))
        {
            return false;
        }

        var valueHash = selected[0].ValueHash;
        if (!HumanInputResponseValueProjector.TryProject(schema, selected[0].Value, out var first)
            || first is null)
        {
            return false;
        }

        return selected.All(candidate => candidate is not null
            && string.Equals(candidate.ValueHash, valueHash, StringComparison.Ordinal)
            && HumanInputResponseValueHash.Matches(candidate.Value, candidate.ValueHash)
            && HumanInputResponseValueProjector.TryProject(schema, candidate.Value, out var projected)
            && Equals(first, projected));
    }

    private static GovernedLoopSequentialHumanInputBindingReadResult Invalid()
        => new(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, null);

    private static GovernedLoopSequentialHumanInputBindingReadResult Unavailable()
        => new(GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable, null);
}
