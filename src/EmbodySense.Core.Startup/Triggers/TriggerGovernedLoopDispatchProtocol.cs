using System.Text;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Prepares and validates canonical governed-publication trigger dispatch without widening runtime authority.</summary>
public static class TriggerGovernedLoopDispatchProtocol
{
    private const string WorkspacePrefix = "workspace-sha256:";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Prepares one exact canonical trigger invocation or returns a local fail-closed disposition.</summary>
    /// <param name="envelope">The selected and revalidated trigger envelope.</param>
    /// <param name="intent">The durable pre-invocation dispatch intent.</param>
    /// <returns>The prepared exact invocation or local disposition.</returns>
    public static TriggerGovernedLoopDispatchPreparation Prepare(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(intent);
        if (!TriggerDispatchOperationId.IsValid(intent.OperationId))
        {
            return Rejection(TriggerDispatchOutcome.NeedsReview, "The durable trigger dispatch intent has a malformed operation identity and cannot be invoked.");
        }

        if (envelope.Loop.Kind != TriggerLoopTargetKind.GovernedPublication
            || envelope.Loop.GovernedPublication is null
            || envelope.Loop.AuthorityGrant is null
            || envelope.Loop.LegacyDefinition is not null
            || !TriggerDeliveryValidator.ValidateLoopReference(envelope.Loop).IsValid)
        {
            return Rejection(TriggerDispatchOutcome.NeedsReview, "The selected trigger target is not one exact governed publication and grant.");
        }

        var payload = envelope.Payload.GetInlinePayload();
        if (payload is null)
        {
            return Rejection(TriggerDispatchOutcome.Rejected, "The governed payload reference requires an adapter that is outside this worker child.");
        }

        string prompt;
        try
        {
            prompt = _strictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return Rejection(TriggerDispatchOutcome.Rejected, "The inline trigger payload is not strict UTF-8 text.");
        }

        return new TriggerGovernedLoopDispatchPreparation(
            new GovernedLoopRunInvocationInput(intent.OperationId, envelope.Loop.GovernedPublication, envelope.Loop.AuthorityGrant, prompt),
            envelope.ActorContext,
            null);
    }

    /// <summary>Maps a canonical runtime response only when exact terminal admission evidence proves its target and context.</summary>
    /// <param name="envelope">The selected and revalidated trigger envelope.</param>
    /// <param name="intent">The durable pre-invocation dispatch intent.</param>
    /// <param name="response">The canonical governed runtime response.</param>
    /// <returns>A proved rejection or accepted outcome, otherwise a needs-review posture.</returns>
    public static TriggerWorkerDispatchResult Map(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, GovernedLoopRunInvocationResponse response)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(response);
        if (!TriggerDispatchOperationId.IsValid(intent.OperationId))
        {
            return NeedsReview(response, "The durable trigger dispatch intent has a malformed operation identity and no runtime response can be trusted for it.");
        }

        var admission = response.AdmissionOutcome;
        if (!MatchesExactAdmission(envelope, intent, admission))
        {
            return NeedsReview(response, "The canonical response did not contain exact validated admission evidence for this trigger target and context.");
        }

        if (!string.Equals(response.AdmissionStatus, admission!.Status, StringComparison.Ordinal))
        {
            return NeedsReview(response, "The canonical admission status projection contradicted its validated terminal outcome.");
        }

        if (string.Equals(admission.Disposition, "Rejected", StringComparison.Ordinal))
        {
            if (admission.Status is not ("Rejected" or "Replayed")
                || !string.Equals(response.Status, "Rejected", StringComparison.Ordinal)
                || admission.RunId is not null
                || string.IsNullOrWhiteSpace(admission.FailureCode)
                || !string.Equals(response.AdmissionFailureCode, admission.FailureCode, StringComparison.Ordinal)
                || response.MaterializationStatus is not null
                || response.ExecutionStatus is not null
                || response.WasDispatched
                || response.Run is not null)
            {
                return NeedsReview(response, "The canonical rejection evidence contradicted the runtime dispatch or run posture.");
            }

            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Rejected, Bound(response.Detail, "Canonical governed admission rejected before provider dispatch."));
        }

        if (!string.Equals(admission.Disposition, "Admitted", StringComparison.Ordinal)
            || admission.Status is not ("Admitted" or "Replayed")
            || admission.FailureCode is not null
            || response.AdmissionFailureCode is not null
            || response.MaterializationStatus is not ("Ready" or "Replayed"))
        {
            return NeedsReview(response, "The canonical admission evidence has an unsupported terminal disposition.");
        }

        var run = response.Run;
        if (run is null
            || !CustomLoopArtifactIdentifier.IsValid(run.Id)
            || !string.Equals(run.Id, admission.RunId, StringComparison.Ordinal)
            || !string.Equals(run.AdmissionOperationId, intent.OperationId, StringComparison.Ordinal)
            || !string.Equals(run.GovernedAdmissionRequestHash, admission.RequestHash, StringComparison.Ordinal)
            || !string.Equals(run.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal)
            || !string.Equals(run.AdmissionActor, envelope.ActorContext.ActorId.Value, StringComparison.Ordinal)
            || !string.Equals(run.Surface, envelope.ActorContext.SurfaceId, StringComparison.Ordinal)
            || !Enum.TryParse<CustomLoopRunStatus>(run.Status, ignoreCase: false, out var runStatus)
            || runStatus == CustomLoopRunStatus.Unknown
            || !MatchesRunStatusProjection(response, runStatus))
        {
            return NeedsReview(response, $"The admitted canonical run did not match the exact operation, request, actor, surface, target, and run binding ({DescribeRunMismatch(envelope, intent, admission, response, run)}).");
        }

        if (runStatus == CustomLoopRunStatus.NeedsReview)
        {
            return NeedsReview(response, "The exact canonical governed run itself requires operator review.");
        }

        if (!TriggerLoopReferenceHash.TryCompute(envelope.Loop, out var loopReferenceHash, out _))
        {
            return NeedsReview(response, "The exact canonical target could not be hashed after admission verification.");
        }

        var governed = new TriggerGovernedInvocationEvidence(intent.OperationId, run.Id, admission.RequestHash, envelope.Loop.LoopId, loopReferenceHash!);
        var outcome = runStatus is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled ? TriggerDispatchOutcome.Terminal : TriggerDispatchOutcome.Accepted;
        return new TriggerWorkerDispatchResult(outcome, Bound(response.Detail, $"Canonical governed run {run.Id} was proved by exact admission evidence."), governed);
    }

    private static bool MatchesExactAdmission(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, GovernedLoopAdmissionOutcomeSnapshot? admission)
        => admission is not null
            && IsHash(admission.RequestHash)
            && IsHash(admission.OutcomeHash)
            && string.Equals(admission.OperationId, intent.OperationId, StringComparison.Ordinal)
            && admission.Publication == envelope.Loop.GovernedPublication
            && admission.AuthorityGrant == envelope.Loop.AuthorityGrant
            && admission.Role?.Identity is not null
            && ContextualRoleId.IsValid(admission.Role.Identity.RoleId)
            && admission.Role.Identity.Revision >= 1
            && IsHash(admission.Role.ContentHash)
            && string.Equals(admission.Role.Identity.RoleId, envelope.ActorContext.RoleId, StringComparison.Ordinal)
            && string.Equals(admission.ActorId, envelope.ActorContext.ActorId.Value, StringComparison.Ordinal)
            && string.Equals(admission.Surface, envelope.ActorContext.SurfaceId, StringComparison.Ordinal)
            && IsHash(envelope.ActorContext.WorkspaceId)
            && string.Equals(admission.WorkspaceId, WorkspacePrefix + envelope.ActorContext.WorkspaceId, StringComparison.Ordinal);

    private static TriggerGovernedLoopDispatchPreparation Rejection(TriggerDispatchOutcome outcome, string detail)
        => new(null, null, new TriggerWorkerDispatchResult(outcome, detail));

    private static string DescribeRunMismatch(
        TriggerDeliveryEnvelope envelope,
        TriggerDispatchEvidence intent,
        GovernedLoopAdmissionOutcomeSnapshot admission,
        GovernedLoopRunInvocationResponse response,
        LoopRunSnapshot? run)
    {
        if (run is null)
        {
            return "run";
        }

        var fields = new List<string>();
        AddMismatch(fields, "runId", CustomLoopArtifactIdentifier.IsValid(run.Id) && string.Equals(run.Id, admission.RunId, StringComparison.Ordinal));
        AddMismatch(fields, "operation", string.Equals(run.AdmissionOperationId, intent.OperationId, StringComparison.Ordinal));
        AddMismatch(fields, "request", string.Equals(run.GovernedAdmissionRequestHash, admission.RequestHash, StringComparison.Ordinal));
        AddMismatch(fields, "target", string.Equals(run.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal));
        AddMismatch(fields, "actor", string.Equals(run.AdmissionActor, envelope.ActorContext.ActorId.Value, StringComparison.Ordinal));
        AddMismatch(fields, "surface", string.Equals(run.Surface, envelope.ActorContext.SurfaceId, StringComparison.Ordinal));
        AddMismatch(fields, "status", Enum.TryParse<CustomLoopRunStatus>(run.Status, ignoreCase: false, out var runStatus)
            && runStatus != CustomLoopRunStatus.Unknown
            && MatchesRunStatusProjection(response, runStatus));
        return fields.Count == 0 ? "unknown" : string.Join(',', fields);
    }

    private static bool MatchesRunStatusProjection(GovernedLoopRunInvocationResponse response, CustomLoopRunStatus runStatus)
        => response.Status switch
        {
            "Executed" => string.Equals(response.ExecutionStatus, runStatus.ToString(), StringComparison.Ordinal),
            "Terminal" => runStatus is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview
                && (response.ExecutionStatus is null || string.Equals(response.ExecutionStatus, runStatus.ToString(), StringComparison.Ordinal)),
            _ => false,
        };

    private static void AddMismatch(ICollection<string> fields, string field, bool matches)
    {
        if (!matches)
        {
            fields.Add(field);
        }
    }

    private static TriggerWorkerDispatchResult NeedsReview(GovernedLoopRunInvocationResponse response, string reason)
        => new(TriggerDispatchOutcome.NeedsReview, Bound(response.Detail, $"{reason} Coordination={response.Status}; ProviderDispatched={response.WasDispatched}."));

    private static string Bound(string detail, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback} {detail.Trim()}";
        return value.Length <= TriggerWorkerLimits.MaxOutcomeDetailCharacters ? value : value[..TriggerWorkerLimits.MaxOutcomeDetailCharacters];
    }

    private static bool IsHash(string? value) => value?.Length == TriggerDeliveryLimits.Sha256HexCharacters && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
