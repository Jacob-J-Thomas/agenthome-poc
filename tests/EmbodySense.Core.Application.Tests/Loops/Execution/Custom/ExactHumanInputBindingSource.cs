using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class ExactHumanInputBindingSource : IGovernedLoopSequentialHumanInputBindingSource
{
    private readonly GovernedLoopTypedValue _value;

    internal ExactHumanInputBindingSource(GovernedLoopTypedValue value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal int ReadCount { get; private set; }

    internal bool IsUnavailable { get; set; }

    internal bool IsInvalid { get; set; }

    internal Exception? ResolveException { get; set; }

    internal Action? BeforeResolve { get; set; }

    internal bool ReturnMalformedReady { get; set; }

    internal GovernedLoopSequentialHumanInputBinding? LastBinding { get; private set; }

    public Task<GovernedLoopSequentialHumanInputBindingReadResult> ResolveAsync(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        BeforeResolve?.Invoke();
        if (ResolveException is not null)
        {
            throw ResolveException;
        }
        if (IsUnavailable)
        {
            return Task.FromResult(new GovernedLoopSequentialHumanInputBindingReadResult(
                GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable,
                null));
        }
        if (IsInvalid)
        {
            return Task.FromResult(Invalid());
        }
        if (ReturnMalformedReady)
        {
            return Task.FromResult(new GovernedLoopSequentialHumanInputBindingReadResult(
                GovernedLoopSequentialHumanInputBindingReadStatus.Ready,
                null));
        }

        var answered = checkpoint.Evidence.ElementAtOrDefault(1);
        if (checkpoint.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            || answered is not { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, AnswerSelection: { } selectionReference })
        {
            return Task.FromResult(Invalid());
        }

        var request = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        var response = new HumanInputResponseReference(
            HumanInputResponseReference.CurrentSchemaVersion,
            "human-input-ordered-reentry-response",
            request,
            new string('a', 64),
            new string('b', 64));
        var selection = HumanInputResponseSelectionHash.Apply(new HumanInputResponseSelection(
            HumanInputResponseSelection.CurrentSchemaVersion,
            "human-input-ordered-reentry-selection",
            request,
            HumanInputResponsePolicyKind.FirstValid,
            ImmutableArray.Create(response),
            null,
            null,
            answered.OccurredAtUtc,
            string.Empty));
        if (!Equals(HumanInputResponseSelectionReference.Create(selection), selectionReference))
        {
            return Task.FromResult(Invalid());
        }

        LastBinding = new GovernedLoopSequentialHumanInputBinding(
            GovernedLoopSequentialHumanInputBinding.CurrentSchemaVersion,
            checkpoint.Binding.CheckpointId,
            selection,
            response,
            _value);
        return Task.FromResult(new GovernedLoopSequentialHumanInputBindingReadResult(
            GovernedLoopSequentialHumanInputBindingReadStatus.Ready,
            LastBinding));
    }

    private static GovernedLoopSequentialHumanInputBindingReadResult Invalid()
        => new(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, null);
}
