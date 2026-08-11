using System.Text;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Prepares and validates trigger custom-loop dispatch evidence without invoking an actuator.</summary>
public static class TriggerCustomLoopDispatchProtocol
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly HashSet<string> _admittedStatuses = new(StringComparer.Ordinal) { "Admitted", "Replayed" };
    private static readonly HashSet<string> _provedPreDispatchRejections = new(StringComparer.Ordinal) { "Conflict", "LimitExceeded", "NonterminalRunExists", "NotFound", "WorkspaceExecutionBusy" };

    /// <summary>Prepares an exact governed invocation request without executing it.</summary>
    /// <param name="envelope">The exact selected trigger envelope.</param>
    /// <param name="intent">The durable dispatch intent.</param>
    /// <returns>Either a prepared invocation input or a proved local rejection.</returns>
    public static TriggerCustomLoopDispatchPreparation Prepare(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(intent);
        if (!TriggerDispatchOperationId.IsValid(intent.OperationId))
        {
            return new TriggerCustomLoopDispatchPreparation(null, null, new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, "The durable trigger dispatch intent has a malformed operation identity and cannot be invoked."));
        }

        var payload = envelope.Payload.GetInlinePayload();
        if (payload is null)
        {
            return new TriggerCustomLoopDispatchPreparation(null, null, new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Rejected, "The governed payload reference requires an adapter that is outside this worker child."));
        }

        string prompt;
        try
        {
            prompt = _strictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return new TriggerCustomLoopDispatchPreparation(null, null, new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Rejected, "The inline trigger payload is not strict UTF-8 text."));
        }

        var input = new LoopRunInvocationInput(envelope.Loop.LoopId, envelope.Loop.DefinitionVersion, envelope.Loop.ContentHash, intent.OperationId, prompt);
        return new TriggerCustomLoopDispatchPreparation(input, envelope.ActorContext, null);
    }

    /// <summary>Maps one governed runtime response only when its closed status and receipt evidence prove the outcome.</summary>
    /// <param name="envelope">The exact selected trigger envelope.</param>
    /// <param name="intent">The durable dispatch intent.</param>
    /// <param name="response">The governed runtime response to validate.</param>
    /// <returns>A proved rejection, exact admitted outcome, or conservative needs-review posture.</returns>
    public static TriggerWorkerDispatchResult Map(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, LoopRunInvocationResponse response)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(response);
        if (!TriggerDispatchOperationId.IsValid(intent.OperationId))
        {
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, "The durable trigger dispatch intent has a malformed operation identity and no runtime response can be trusted for it.");
        }

        if (_admittedStatuses.Contains(response.AdmissionStatus))
        {
            return MapAdmitted(envelope, intent, response);
        }

        if (!response.WasDispatched && _provedPreDispatchRejections.Contains(response.AdmissionStatus))
        {
            var rejectionDetail = string.IsNullOrWhiteSpace(response.Detail) ? $"The governed runner proved pre-dispatch rejection with admission status {response.AdmissionStatus}." : response.Detail;
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Rejected, rejectionDetail);
        }

        return NeedsReview(response, "The governed invocation posture does not prove either pre-dispatch rejection or an exact admitted receipt.");
    }

    private static TriggerWorkerDispatchResult MapAdmitted(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, LoopRunInvocationResponse response)
    {
        var run = response.Run;
        if (run is null
            || !CustomLoopArtifactIdentifier.IsValid(run.Id)
            || !string.Equals(run.AdmissionOperationId, intent.OperationId, StringComparison.Ordinal)
            || !IsHash(run.AdmissionRequestHash)
            || !string.Equals(run.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal)
            || run.AdmittedDefinition is null
            || !string.Equals(run.AdmittedDefinition.Id, envelope.Loop.LoopId, StringComparison.Ordinal)
            || run.AdmittedDefinition.DefinitionVersion != envelope.Loop.DefinitionVersion
            || !string.Equals(run.AdmittedDefinition.ContentHash, envelope.Loop.ContentHash, StringComparison.Ordinal)
            || !Enum.TryParse<CustomLoopRunStatus>(run.Status, ignoreCase: false, out var runStatus)
            || runStatus == CustomLoopRunStatus.Unknown
            || !string.Equals(response.ExecutionStatus, run.Status, StringComparison.Ordinal))
        {
            return NeedsReview(response, "The admitted response was missing or did not match the exact operation, request, definition, and run receipt binding.");
        }

        if (runStatus == CustomLoopRunStatus.NeedsReview)
        {
            return NeedsReview(response, "The exact governed run itself requires operator review.");
        }

        var governed = new TriggerGovernedInvocationEvidence(run.AdmissionOperationId, run.Id, run.AdmissionRequestHash, run.LoopId, run.AdmittedDefinition.DefinitionVersion, run.AdmittedDefinition.ContentHash);
        var outcome = runStatus is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled ? TriggerDispatchOutcome.Terminal : TriggerDispatchOutcome.Accepted;
        var detail = $"Admission={response.AdmissionStatus}; Execution={response.ExecutionStatus}; Run={run.Id}; ProviderDispatched={response.WasDispatched}; {response.Detail}";
        return new TriggerWorkerDispatchResult(outcome, detail, governed);
    }

    private static TriggerWorkerDispatchResult NeedsReview(LoopRunInvocationResponse response, string reason)
    {
        return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, $"{reason} Admission={response.AdmissionStatus}; Execution={response.ExecutionStatus ?? "Unknown"}; ProviderDispatched={response.WasDispatched}; {response.Detail}");
    }

    private static bool IsHash(string value) => value.Length == CustomLoopLimits.Sha256HexCharacters && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
