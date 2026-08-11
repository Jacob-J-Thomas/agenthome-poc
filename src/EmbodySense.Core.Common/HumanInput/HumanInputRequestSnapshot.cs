using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput;

/// <summary>Creates bounded deep snapshots of mutable-array Human Input request contracts before durable use.</summary>
public static class HumanInputRequestSnapshot
{
    /// <summary>Captures and validates a deep independent request snapshot without enumerating beyond schema-1 bounds.</summary>
    /// <param name="request">The potentially caller-owned request.</param>
    /// <param name="snapshot">The validated deep snapshot when successful.</param>
    /// <param name="validation">The deterministic validation result for the captured shape.</param>
    /// <returns><see langword="true"/> when a complete valid snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCapture(HumanInputRequest? request, out HumanInputRequest? snapshot, out HumanInputValidationResult validation)
    {
        if (!IsBounded(request))
        {
            snapshot = null;
            validation = new HumanInputValidationResult([new HumanInputValidationError("request_snapshot_unbounded", "$", "Human-input request collections must remain within schema-1 snapshot bounds.")]);
            return false;
        }

        if (request is null)
        {
            snapshot = null;
            validation = HumanInputValidator.ValidateRequest(null);
            return false;
        }

        try
        {
            snapshot = new HumanInputRequest(
                request.SchemaVersion,
                request.RequestId,
                request.RequestVersionId,
                Snapshot(request.Binding),
                request.Purpose,
                request.Prompt,
                Snapshot(request.ResponseSchema),
                request.PrivacyClass,
                Snapshot(request.EligibleRespondents),
                Snapshot(request.Timing),
                Snapshot(request.ResponsePolicy),
                Snapshot(request.ContinuationBinding),
                request.RequestHash);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = new HumanInputValidationResult([new HumanInputValidationError("request_snapshot_unstable", "$", "Human-input request shape changed while its bounded snapshot was captured.")]);
            return false;
        }

        validation = HumanInputValidator.ValidateRequest(snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    private static bool IsBounded(HumanInputRequest? request)
    {
        if (request is null)
        {
            return true;
        }

        if (request.EligibleRespondents is { Length: > HumanInputLimits.MaxEligibleRespondents }
            || request.ResponseSchema?.Choices is { Length: > HumanInputLimits.MaxChoices }
            || request.ResponseSchema?.StructuredFields is { Length: > HumanInputLimits.MaxStructuredFields })
        {
            return false;
        }

        if (request.ResponseSchema?.StructuredFields is { } fields)
        {
            for (var index = 0; index < fields.Length; index++)
            {
                if (fields[index]?.Choices is { Length: > HumanInputLimits.MaxChoices })
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static HumanInputRequestBinding Snapshot(HumanInputRequestBinding? value) => value is null
        ? null!
        : new HumanInputRequestBinding(value.WorkspaceId, value.LoopGraphId, value.LoopRevisionId, value.NodeId, value.RunId, value.CheckpointId);

    private static HumanInputResponseSchema Snapshot(HumanInputResponseSchema? value) => value is null
        ? null!
        : new HumanInputResponseSchema(value.Kind, value.MaxTextCharacters, Snapshot(value.Choices), Snapshot(value.StructuredFields), Snapshot(value.ReferencePolicy));

    private static HumanInputChoice[]? Snapshot(HumanInputChoice[]? values) => values?.Select(value => value is null ? null! : new HumanInputChoice(value.ChoiceId, value.DisplayText)).ToArray();

    private static HumanInputStructuredFieldSchema[]? Snapshot(HumanInputStructuredFieldSchema[]? values) => values?.Select(value => value is null ? null! : new HumanInputStructuredFieldSchema(value.FieldId, value.Kind, value.Required, value.MaxTextCharacters, Snapshot(value.Choices))).ToArray();

    private static HumanInputReferencePolicy? Snapshot(HumanInputReferencePolicy? value) => value is null ? null : new HumanInputReferencePolicy(value.Kind, value.MaxReferenceCharacters);

    private static HumanInputEligibleRespondent[] Snapshot(HumanInputEligibleRespondent[]? values) => values?.Select(value => value is null ? null! : new HumanInputEligibleRespondent(value.RespondentId, value.RoutingReference)).ToArray()!;

    private static HumanInputTiming Snapshot(HumanInputTiming? value) => value is null ? null! : new HumanInputTiming(value.RequestedAtUtc, value.ExpiresAtUtc);

    private static HumanInputResponsePolicy Snapshot(HumanInputResponsePolicy? value) => value is null ? null! : new HumanInputResponsePolicy(value.Kind);

    private static HumanInputContinuationBinding Snapshot(HumanInputContinuationBinding? value) => value is null ? null! : new HumanInputContinuationBinding(value.Kind, value.NodeId, value.CheckpointId);
}
