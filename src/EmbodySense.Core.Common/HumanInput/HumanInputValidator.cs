using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Validates schema-1 human-input request and response boundaries without delivering, persisting, authorizing, or exposing their data.
/// </summary>
public static class HumanInputValidator
{
    /// <summary>
    /// Validates a complete human-input request and its canonical hash.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>Every deterministic request-boundary error.</returns>
    public static HumanInputValidationResult ValidateRequest(HumanInputRequest? request)
    {
        var errors = new List<HumanInputValidationError>();
        if (request is null)
        {
            Add(errors, "request_required", "$", "A human-input request is required.");
            return new HumanInputValidationResult(errors);
        }

        if (request.SchemaVersion != HumanInputRequest.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_schema_version", "schemaVersion", "Human-input request schema version must be 1.");
        }

        ValidateId(request.RequestId, "requestId", errors);
        ValidateId(request.RequestVersionId, "requestVersionId", errors);
        ValidateBinding(request.Binding, "binding", errors);
        ValidateText(request.Purpose, "purpose", HumanInputLimits.MaxPurposeCharacters, true, errors);
        ValidateText(request.Prompt, "prompt", HumanInputLimits.MaxPromptCharacters, true, errors);
        ValidateSchema(request.ResponseSchema, "responseSchema", errors);
        if (!Enum.IsDefined(request.PrivacyClass) || request.PrivacyClass == HumanInputPrivacyClass.Unknown)
        {
            Add(errors, "invalid_privacy_class", "privacyClass", "A supported privacy class is required.");
        }

        ValidateRespondents(request.EligibleRespondents, errors);
        ValidateTiming(request.Timing, errors);
        if (request.ResponsePolicy is null || request.ResponsePolicy.Kind != HumanInputResponsePolicyKind.FirstEligibleResponse)
        {
            Add(errors, "unsupported_response_policy", "responsePolicy", "Schema 1 requires the explicit first-eligible-response policy.");
        }

        ValidateContinuation(request.ContinuationBinding, request.Binding, errors);
        if (!IsSha256(request.RequestHash))
        {
            Add(errors, "invalid_request_hash", "requestHash", "Request hash must be a lowercase SHA-256 digest.");
        }
        else if (!HumanInputRequestHash.IsBoundedForCanonicalization(request))
        {
            Add(errors, "request_hash_not_computable", "requestHash", "Request values or collections exceed canonicalization limits; the hash was not recomputed.");
        }
        else if (!HumanInputRequestHash.Matches(request))
        {
            Add(errors, "request_hash_mismatch", "requestHash", "Request hash does not match the canonical request contract.");
        }

        return new HumanInputValidationResult(errors);
    }

    /// <summary>
    /// Validates an untrusted response against one exact request boundary and returns no lifecycle, storage, or authority decision.
    /// </summary>
    /// <param name="request">The request that defines the only acceptable binding and schema.</param>
    /// <param name="response">The untrusted response to validate.</param>
    /// <returns>A typed valid or invalid boundary outcome.</returns>
    /// <remarks>An invalid request fails immediately with only request-validation errors; no member of <paramref name="response"/> is inspected.</remarks>
    public static HumanInputResponseOutcome ValidateResponse(HumanInputRequest? request, HumanInputResponse? response)
    {
        var errors = ValidateRequest(request).Errors.ToList();
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        if (response is null)
        {
            Add(errors, "response_required", "$", "A human-input response is required.");
            return Invalid(errors);
        }

        if (request is null)
        {
            return Invalid(errors);
        }

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            Add(errors, "request_id_mismatch", "requestId", "Response request ID must exactly match the request.");
        }

        if (!string.Equals(response.RequestVersionId, request.RequestVersionId, StringComparison.Ordinal))
        {
            Add(errors, "request_version_mismatch", "requestVersionId", "Response request version ID must exactly match the request.");
        }

        if (!Equals(response.Binding, request.Binding))
        {
            Add(errors, "binding_mismatch", "binding", "Response workspace, loop revision, node, run, and checkpoint binding must exactly match the request.");
        }

        ValidateId(response.AuthenticatedActorRef, "authenticatedActorRef", errors);
        if (!IsEligibleRespondent(request.EligibleRespondents, response.AuthenticatedActorRef))
        {
            Add(errors, "ineligible_respondent", "authenticatedActorRef", "Authenticated actor is not an explicit eligible respondent.");
        }

        if (!IsUtc(response.SubmittedAtUtc) || request.Timing is null || response.SubmittedAtUtc < request.Timing.RequestedAtUtc || response.SubmittedAtUtc > request.Timing.ExpiresAtUtc)
        {
            Add(errors, "submission_outside_window", "submittedAtUtc", "Submission time must be UTC and within the exact request response window.");
        }

        ValidateOptionalText(response.Explanation, "explanation", HumanInputLimits.MaxExplanationCharacters, errors);
        ValidateResponseValue(request.ResponseSchema, response.Value, errors);
        return errors.Count == 0
            ? new HumanInputResponseOutcome(HumanInputResponseOutcomeKind.Valid, response, [])
            : Invalid(errors);
    }

    private static HumanInputResponseOutcome Invalid(IReadOnlyList<HumanInputValidationError> errors) => new(HumanInputResponseOutcomeKind.Invalid, null, errors);

    private static void ValidateBinding(HumanInputRequestBinding? binding, string field, List<HumanInputValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, "binding_required", field, "An exact workspace, loop revision, node, run, and checkpoint binding is required.");
            return;
        }

        ValidateId(binding.WorkspaceId, $"{field}.workspaceId", errors);
        ValidateId(binding.LoopRevisionId, $"{field}.loopRevisionId", errors);
        ValidateId(binding.NodeId, $"{field}.nodeId", errors);
        ValidateId(binding.RunId, $"{field}.runId", errors);
        ValidateId(binding.CheckpointId, $"{field}.checkpointId", errors);
    }

    private static void ValidateRespondents(HumanInputEligibleRespondent[]? respondents, List<HumanInputValidationError> errors)
    {
        if (respondents is null || respondents.Length is < 1 or > HumanInputLimits.MaxEligibleRespondents)
        {
            Add(errors, "invalid_respondent_count", "eligibleRespondents", "At least one and no more than the bounded number of explicitly eligible respondents is required.");
            return;
        }

        var respondentIds = new HashSet<string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < respondents.Length; index++)
        {
            var respondent = respondents[index];
            var field = $"eligibleRespondents[{index}]";
            if (respondent is null)
            {
                Add(errors, "respondent_required", field, "Eligible respondent cannot be null.");
                continue;
            }

            ValidateId(respondent.RespondentId, $"{field}.respondentId", errors);
            ValidateText(respondent.RoutingReference, $"{field}.routingReference", HumanInputLimits.MaxRoutingReferenceCharacters, true, errors);
            if (!respondentIds.Add(respondent.RespondentId ?? string.Empty))
            {
                Add(errors, "duplicate_respondent", $"{field}.respondentId", "Eligible respondent IDs must be unique.");
            }

            if (!routes.Add(respondent.RoutingReference ?? string.Empty))
            {
                Add(errors, "ambiguous_recipient_route", $"{field}.routingReference", "Each eligible respondent requires one unique routing reference.");
            }
        }
    }

    private static void ValidateTiming(HumanInputTiming? timing, List<HumanInputValidationError> errors)
    {
        if (timing is null || !IsUtc(timing.RequestedAtUtc) || !IsUtc(timing.ExpiresAtUtc))
        {
            Add(errors, "invalid_timing", "timing", "Timing requires non-default UTC request and expiry values.");
            return;
        }

        var window = timing.ExpiresAtUtc - timing.RequestedAtUtc;
        if (window < HumanInputLimits.MinResponseWindow || window > HumanInputLimits.MaxResponseWindow)
        {
            Add(errors, "unbounded_timing", "timing.expiresAtUtc", "Response timing must be a bounded window within the schema-1 minimum and maximum.");
        }
    }

    private static void ValidateContinuation(HumanInputContinuationBinding? continuation, HumanInputRequestBinding? binding, List<HumanInputValidationError> errors)
    {
        if (continuation is null || continuation.Kind != HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly)
        {
            Add(errors, "unsupported_continuation_policy", "continuationBinding", "Schema 1 permits only exact bound-node and checkpoint data visibility.");
            return;
        }

        ValidateId(continuation.NodeId, "continuationBinding.nodeId", errors);
        ValidateId(continuation.CheckpointId, "continuationBinding.checkpointId", errors);
        if (binding is not null && (!string.Equals(continuation.NodeId, binding.NodeId, StringComparison.Ordinal) || !string.Equals(continuation.CheckpointId, binding.CheckpointId, StringComparison.Ordinal)))
        {
            Add(errors, "continuation_authority_widening", "continuationBinding", "Continuation visibility must remain exact-bound to the request node and checkpoint.");
        }
    }

    private static void ValidateSchema(HumanInputResponseSchema? schema, string field, List<HumanInputValidationError> errors)
    {
        if (schema is null || !Enum.IsDefined(schema.Kind) || schema.Kind == HumanInputResponseKind.Unknown)
        {
            Add(errors, "unsupported_response_kind", field, "A supported response schema kind is required.");
            return;
        }

        switch (schema.Kind)
        {
            case HumanInputResponseKind.Text:
                ValidateMaximum(schema.MaxTextCharacters, $"{field}.maxTextCharacters", errors);
                RequireAbsent(schema.Choices, $"{field}.choices", errors);
                RequireAbsent(schema.StructuredFields, $"{field}.structuredFields", errors);
                RequireAbsent(schema.ReferencePolicy, $"{field}.referencePolicy", errors);
                break;
            case HumanInputResponseKind.Choice:
                RequireAbsent(schema.MaxTextCharacters, $"{field}.maxTextCharacters", errors);
                ValidateChoices(schema.Choices, $"{field}.choices", errors);
                RequireAbsent(schema.StructuredFields, $"{field}.structuredFields", errors);
                RequireAbsent(schema.ReferencePolicy, $"{field}.referencePolicy", errors);
                break;
            case HumanInputResponseKind.Confirmation:
                RequireAbsent(schema.MaxTextCharacters, $"{field}.maxTextCharacters", errors);
                RequireAbsent(schema.Choices, $"{field}.choices", errors);
                RequireAbsent(schema.StructuredFields, $"{field}.structuredFields", errors);
                RequireAbsent(schema.ReferencePolicy, $"{field}.referencePolicy", errors);
                break;
            case HumanInputResponseKind.Structured:
                RequireAbsent(schema.MaxTextCharacters, $"{field}.maxTextCharacters", errors);
                RequireAbsent(schema.Choices, $"{field}.choices", errors);
                ValidateStructuredFields(schema.StructuredFields, $"{field}.structuredFields", errors);
                RequireAbsent(schema.ReferencePolicy, $"{field}.referencePolicy", errors);
                break;
            case HumanInputResponseKind.Reference:
                RequireAbsent(schema.MaxTextCharacters, $"{field}.maxTextCharacters", errors);
                RequireAbsent(schema.Choices, $"{field}.choices", errors);
                RequireAbsent(schema.StructuredFields, $"{field}.structuredFields", errors);
                ValidateReferencePolicy(schema.ReferencePolicy, $"{field}.referencePolicy", errors);
                break;
            default:
                Add(errors, "unsupported_response_kind", field, "Response schema kind is unsupported.");
                break;
        }
    }

    private static void ValidateStructuredFields(HumanInputStructuredFieldSchema[]? fields, string field, List<HumanInputValidationError> errors)
    {
        if (fields is null || fields.Length is < 1 or > HumanInputLimits.MaxStructuredFields)
        {
            Add(errors, "invalid_structured_field_count", field, "Structured response schemas require a bounded non-empty field list.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Length; index++)
        {
            var item = fields[index];
            var itemField = $"{field}[{index}]";
            if (item is null)
            {
                Add(errors, "structured_field_required", itemField, "Structured field cannot be null.");
                continue;
            }

            ValidateId(item.FieldId, $"{itemField}.fieldId", errors);
            if (!ids.Add(item.FieldId ?? string.Empty))
            {
                Add(errors, "duplicate_structured_field", $"{itemField}.fieldId", "Structured field IDs must be unique.");
            }

            if (item.Kind == HumanInputStructuredFieldKind.Text)
            {
                ValidateMaximum(item.MaxTextCharacters, $"{itemField}.maxTextCharacters", errors);
                RequireAbsent(item.Choices, $"{itemField}.choices", errors);
            }
            else if (item.Kind == HumanInputStructuredFieldKind.Choice)
            {
                RequireAbsent(item.MaxTextCharacters, $"{itemField}.maxTextCharacters", errors);
                ValidateChoices(item.Choices, $"{itemField}.choices", errors);
            }
            else
            {
                Add(errors, "unsupported_structured_field_kind", $"{itemField}.kind", "Structured field kind is unsupported.");
            }
        }
    }

    private static void ValidateChoices(HumanInputChoice[]? choices, string field, List<HumanInputValidationError> errors)
    {
        if (choices is null || choices.Length is < 2 or > HumanInputLimits.MaxChoices)
        {
            Add(errors, "invalid_choice_count", field, "Choice schema requires two through the bounded maximum number of choices.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = choices[index];
            var itemField = $"{field}[{index}]";
            if (choice is null)
            {
                Add(errors, "choice_required", itemField, "Choice cannot be null.");
                continue;
            }

            ValidateId(choice.ChoiceId, $"{itemField}.choiceId", errors);
            ValidateText(choice.DisplayText, $"{itemField}.displayText", HumanInputLimits.MaxChoiceDisplayCharacters, true, errors);
            if (!ids.Add(choice.ChoiceId ?? string.Empty))
            {
                Add(errors, "duplicate_choice", $"{itemField}.choiceId", "Choice IDs must be unique.");
            }
        }
    }

    private static void ValidateReferencePolicy(HumanInputReferencePolicy? policy, string field, List<HumanInputValidationError> errors)
    {
        if (policy is null || !Enum.IsDefined(policy.Kind) || policy.Kind == HumanInputReferenceKind.Unknown || policy.MaxReferenceCharacters is < 1 or > HumanInputLimits.MaxReferenceCharacters)
        {
            Add(errors, "invalid_reference_policy", field, "Reference response schemas require one bounded supported safe-reference policy.");
        }
    }

    private static void ValidateResponseValue(HumanInputResponseSchema? schema, HumanInputResponseValue? value, List<HumanInputValidationError> errors)
    {
        if (schema is null || value is null || value.Kind != schema.Kind)
        {
            Add(errors, "response_value_schema_mismatch", "value", "Response value must use the request's exact schema kind.");
            return;
        }

        switch (value.Kind)
        {
            case HumanInputResponseKind.Text:
                ValidateSchemaBoundedText(value.Text, "value.text", schema.MaxTextCharacters, errors);
                RequireAbsent(value.ChoiceId, "value.choiceId", errors);
                RequireAbsent(value.Confirmation, "value.confirmation", errors);
                RequireAbsent(value.StructuredFields, "value.structuredFields", errors);
                RequireAbsent(value.Reference, "value.reference", errors);
                break;
            case HumanInputResponseKind.Choice:
                ValidateSelectedChoice(value.ChoiceId, schema.Choices, "value.choiceId", errors);
                RequireAbsent(value.Text, "value.text", errors);
                RequireAbsent(value.Confirmation, "value.confirmation", errors);
                RequireAbsent(value.StructuredFields, "value.structuredFields", errors);
                RequireAbsent(value.Reference, "value.reference", errors);
                break;
            case HumanInputResponseKind.Confirmation:
                if (value.Confirmation is null)
                {
                    Add(errors, "confirmation_required", "value.confirmation", "A confirmation response requires a boolean data value.");
                }

                RequireAbsent(value.Text, "value.text", errors);
                RequireAbsent(value.ChoiceId, "value.choiceId", errors);
                RequireAbsent(value.StructuredFields, "value.structuredFields", errors);
                RequireAbsent(value.Reference, "value.reference", errors);
                break;
            case HumanInputResponseKind.Structured:
                ValidateStructuredValues(value.StructuredFields, schema.StructuredFields, errors);
                RequireAbsent(value.Text, "value.text", errors);
                RequireAbsent(value.ChoiceId, "value.choiceId", errors);
                RequireAbsent(value.Confirmation, "value.confirmation", errors);
                RequireAbsent(value.Reference, "value.reference", errors);
                break;
            case HumanInputResponseKind.Reference:
                ValidateReference(value.Reference, schema.ReferencePolicy, errors);
                RequireAbsent(value.Text, "value.text", errors);
                RequireAbsent(value.ChoiceId, "value.choiceId", errors);
                RequireAbsent(value.Confirmation, "value.confirmation", errors);
                RequireAbsent(value.StructuredFields, "value.structuredFields", errors);
                break;
        }
    }

    private static void ValidateStructuredValues(HumanInputStructuredFieldValue[]? values, HumanInputStructuredFieldSchema[]? schema, List<HumanInputValidationError> errors)
    {
        if (values is null || schema is null || values.Length > HumanInputLimits.MaxStructuredFields || schema.Length > HumanInputLimits.MaxStructuredFields)
        {
            Add(errors, "invalid_structured_values", "value.structuredFields", "Structured values must be a bounded subset of the declared fields.");
            return;
        }

        if (values.Length > schema.Length)
        {
            Add(errors, "invalid_structured_values", "value.structuredFields", "Structured values must be a bounded subset of the declared fields.");
        }

        var declared = new Dictionary<string, HumanInputStructuredFieldSchema>(StringComparer.Ordinal);
        for (var index = 0; index < schema.Length; index++)
        {
            var field = schema[index];
            if (field is not null && HumanInputIdentifier.IsValid(field.FieldId))
            {
                declared.TryAdd(field.FieldId, field);
            }
        }

        var submitted = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var field = $"value.structuredFields[{index}]";
            if (value is null || !declared.TryGetValue(value.FieldId ?? string.Empty, out var fieldSchema))
            {
                Add(errors, "unknown_structured_field", field, "Structured response contains an undeclared field.");
                continue;
            }

            if (!submitted.Add(value.FieldId ?? string.Empty))
            {
                Add(errors, "duplicate_structured_value", $"{field}.fieldId", "Structured response field IDs must be unique.");
            }

            if (fieldSchema.Kind == HumanInputStructuredFieldKind.Text)
            {
                ValidateSchemaBoundedText(value.Text, $"{field}.text", fieldSchema.MaxTextCharacters, errors);
                RequireAbsent(value.ChoiceId, $"{field}.choiceId", errors);
            }
            else if (fieldSchema.Kind == HumanInputStructuredFieldKind.Choice)
            {
                ValidateSelectedChoice(value.ChoiceId, fieldSchema.Choices, $"{field}.choiceId", errors);
                RequireAbsent(value.Text, $"{field}.text", errors);
            }
        }

        for (var index = 0; index < schema.Length; index++)
        {
            var required = schema[index];
            if (required is { Required: true } && HumanInputIdentifier.IsValid(required.FieldId) && !submitted.Contains(required.FieldId))
            {
                Add(errors, "required_structured_field_missing", "value.structuredFields", "Structured response omitted a required field.");
            }
        }
    }

    private static void ValidateReference(HumanInputReference? reference, HumanInputReferencePolicy? policy, List<HumanInputValidationError> errors)
    {
        if (reference is null
            || policy is null
            || policy.MaxReferenceCharacters is < 1 or > HumanInputLimits.MaxReferenceCharacters
            || reference.Kind != policy.Kind
            || !HumanInputIdentifier.IsValid(reference.Value, policy.MaxReferenceCharacters))
        {
            Add(errors, "invalid_safe_reference", "value.reference", "Response reference must use the exact declared safe kind and a bounded opaque identifier.");
        }
    }

    private static void ValidateSelectedChoice(string? choiceId, HumanInputChoice[]? choices, string field, List<HumanInputValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(choiceId) || !ContainsDeclaredChoice(choices, choiceId))
        {
            Add(errors, "invalid_selected_choice", field, "Response must select one declared choice ID.");
        }
    }

    private static void ValidateMaximum(int? maximum, string field, List<HumanInputValidationError> errors)
    {
        if (maximum is not { } boundedMaximum || boundedMaximum < 1 || boundedMaximum > HumanInputLimits.MaxResponseTextCharacters)
        {
            Add(errors, "invalid_text_limit", field, "Text limit must be positive and within the schema-1 maximum.");
        }
    }

    private static bool ContainsDeclaredChoice(HumanInputChoice[]? choices, string? choiceId)
    {
        if (choices is null || choices.Length is < 2 or > HumanInputLimits.MaxChoices)
        {
            return false;
        }

        for (var index = 0; index < choices.Length; index++)
        {
            if (choices[index] is { } choice && string.Equals(choice.ChoiceId, choiceId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEligibleRespondent(HumanInputEligibleRespondent[]? respondents, string actorRef)
    {
        if (respondents is null || respondents.Length is < 1 or > HumanInputLimits.MaxEligibleRespondents)
        {
            return false;
        }

        for (var index = 0; index < respondents.Length; index++)
        {
            if (respondents[index] is { } respondent && string.Equals(respondent.RespondentId, actorRef, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateId(string? value, string field, List<HumanInputValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value))
        {
            Add(errors, "invalid_identifier", field, "Value must be a bounded canonical lowercase ASCII identifier.");
        }
    }

    private static void ValidateText(string? value, string field, int maximum, bool required, List<HumanInputValidationError> errors)
    {
        if (!HumanInputText.IsValid(value, maximum, required))
        {
            Add(errors, "invalid_text", field, "Text must be bounded canonical Unicode without unsafe characters.");
        }
    }

    private static void ValidateOptionalText(string? value, string field, int maximum, List<HumanInputValidationError> errors)
    {
        if (value is not null)
        {
            ValidateText(value, field, maximum, false, errors);
        }
    }

    private static void ValidateSchemaBoundedText(string? value, string field, int? maximum, List<HumanInputValidationError> errors)
    {
        if (maximum is not { } boundedMaximum || boundedMaximum < 1 || boundedMaximum > HumanInputLimits.MaxResponseTextCharacters)
        {
            Add(errors, "invalid_text_limit", field, "Response text limit must be positive and within the schema-1 maximum.");
            return;
        }

        ValidateText(value, field, boundedMaximum, true, errors);
    }

    private static void RequireAbsent(object? value, string field, List<HumanInputValidationError> errors)
    {
        if (value is not null)
        {
            Add(errors, "unexpected_response_member", field, "Member is not permitted for this typed response shape.");
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsSha256(string? value) => value is { Length: HumanInputLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Add(List<HumanInputValidationError> errors, string code, string field, string message) => errors.Add(new HumanInputValidationError(code, field, message));
}
