using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Creates bounded deep snapshots of authenticated response-operation evidence before durable use.</summary>
public static class HumanInputResponseOperationEvidenceSnapshot
{
    /// <summary>Captures and validates an independent bounded evidence snapshot.</summary>
    /// <param name="evidence">The potentially caller-owned operation evidence.</param>
    /// <param name="snapshot">The validated deep snapshot when successful.</param>
    /// <param name="validation">The deterministic response validation result.</param>
    /// <returns><see langword="true"/> when a complete valid snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCapture(
        HumanInputResponseOperationEvidence? evidence,
        out HumanInputResponseOperationEvidence? snapshot,
        out HumanInputResponseValidationResult validation)
    {
        if (evidence is null
            || evidence.TargetResponses.IsDefault
            || evidence.TargetResponses.Length > HumanInputResponseContractLimits.MaxSelectedResponses)
        {
            snapshot = null;
            validation = HumanInputResponseContractValidator.ValidateEvidence(evidence);
            return false;
        }

        try
        {
            var targets = new HumanInputResponseReference[evidence.TargetResponses.Length];
            for (var index = 0; index < targets.Length; index++)
            {
                targets[index] = Snapshot(evidence.TargetResponses[index]);
            }

            HumanInputResponseArtifact? attemptedResponse = null;
            if (evidence.AttemptedResponse is not null
                && (!HumanInputResponseArtifactSnapshot.TryCaptureBoundedAttempt(evidence.AttemptedResponse, out attemptedResponse, out _)
                    || attemptedResponse is null))
            {
                snapshot = null;
                validation = new HumanInputResponseValidationResult(
                    [new HumanInputResponseValidationError(HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.attemptedResponse", "The attempted response must be a stable bounded artifact with matching hashes.")]);
                return false;
            }

            snapshot = evidence with
            {
                Request = Snapshot(evidence.Request),
                ExpectedBinding = Snapshot(evidence.ExpectedBinding),
                ObservedBinding = evidence.ObservedBinding is null ? null : Snapshot(evidence.ObservedBinding),
                PreviousHead = evidence.PreviousHead is null ? null : Snapshot(evidence.PreviousHead),
                ResultHead = evidence.ResultHead is null ? null : Snapshot(evidence.ResultHead),
                AttemptedResponse = attemptedResponse,
                SubmittedResponse = evidence.SubmittedResponse is null ? null : Snapshot(evidence.SubmittedResponse),
                TargetResponses = targets.ToImmutableArray(),
                Selection = evidence.Selection is null ? null : Snapshot(evidence.Selection)
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = new HumanInputResponseValidationResult(
                [new HumanInputResponseValidationError(HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$", "The bounded response evidence changed while its snapshot was captured.")]);
            return false;
        }

        validation = HumanInputResponseContractValidator.ValidateEvidence(snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    private static HumanInputRequestBinding Snapshot(HumanInputRequestBinding value)
        => new(value.WorkspaceId, value.LoopGraphId, value.LoopRevisionId, value.NodeId, value.RunId, value.CheckpointId);

    private static HumanInputRequestReference Snapshot(HumanInputRequestReference value)
        => value with { };

    private static HumanInputResponseReference Snapshot(HumanInputResponseReference value)
        => value with { Request = Snapshot(value.Request) };

    private static HumanInputResponseSelectionReference Snapshot(HumanInputResponseSelectionReference value)
        => value with { Request = Snapshot(value.Request) };

    private static HumanInputRequestLifecycleHead Snapshot(HumanInputRequestLifecycleHead value)
        => value with
        {
            CurrentRequest = Snapshot(value.CurrentRequest),
            AnswerSelection = value.AnswerSelection is null ? null : Snapshot(value.AnswerSelection)
        };
}
