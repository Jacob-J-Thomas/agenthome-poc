using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Serializes and parses only exact canonical schema-1 Human Input waiting-checkpoint JSON without providing a compatibility reader or migration path.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointContractJson
{
    private static readonly string[] _bindingProperties = ["admissionReceiptHash", "activationOrdinal", "checkpointId", "cycleId", "cycleIteration", "execution", "frontierHash", "frontierVersion", "graphArtifactHash", "graphLayoutHash", "nodeId", "nodeVisitOrdinal", "publication", "schemaVersion", "workspaceId"];
    private static readonly string[] _choiceProperties = ["choiceId", "displayText"];
    private static readonly string[] _configProperties = ["eligibleRespondents", "failurePolicyReference", "privacyClass", "prompt", "purpose", "requestSchemaReference", "responsePolicy", "responseSchema", "schemaVersion", "timeoutPolicyReference"];
    private static readonly string[] _continuationProperties = ["checkpointId", "kind", "nodeId"];
    private static readonly string[] _evidenceProperties = ["answerSelection", "evidenceHash", "kind", "occurredAtUtc", "previousEvidenceHash", "schemaVersion", "sequence", "supersedingCheckpointHash", "supersedingCheckpointId", "terminalizationReceiptHash", "terminalizationReceiptId"];
    private static readonly string[] _executionProperties = ["executionGeneration", "revision", "runId", "schemaVersion"];
    private static readonly string[] _policyProperties = ["kind", "orderedRoleIds", "requiredResponseCount"];
    private static readonly string[] _resolvedPolicyProperties = ["actorId", "expiresAtUtc", "failurePolicy", "graphId", "graphRevisionId", "nodeId", "resolvedAtUtc", "resolutionHash", "schemaVersion", "terminalDisposition", "timeoutPolicy", "workspaceId"];
    private static readonly string[] _resolvedPolicyArtifactProperties = ["authorityActorId", "contentHash", "graphId", "kind", "policyId", "responseWindowMilliseconds", "revisionId", "schemaVersion", "terminalDisposition", "workspaceId"];
    private static readonly string[] _publicationProperties = ["publicationOperationId", "revision", "schemaVersion", "validationEvidenceHash"];
    private static readonly string[] _referencePolicyProperties = ["kind", "maxReferenceCharacters"];
    private static readonly string[] _requestBindingProperties = ["checkpointId", "loopGraphId", "loopRevisionId", "nodeId", "runId", "workspaceId"];
    private static readonly string[] _requestProperties = ["binding", "eligibleRespondents", "privacyClass", "prompt", "purpose", "requestHash", "requestId", "requestVersionId", "responsePolicy", "responseSchema", "schemaVersion", "timing", "continuationBinding"];
    private static readonly string[] _requestReferenceProperties = ["requestHash", "requestId", "requestVersionId", "schemaVersion"];
    private static readonly string[] _respondentProperties = ["respondentId", "respondentRoleId", "routingReference"];
    private static readonly string[] _responseSchemaProperties = ["choices", "kind", "maxTextCharacters", "referencePolicy", "structuredFields"];
    private static readonly string[] _revisionProperties = ["executableHash", "graphId", "revisionId", "schemaVersion"];
    private static readonly string[] _selectionProperties = ["request", "schemaVersion", "selectionHash", "selectionId"];
    private static readonly string[] _stateProperties = ["binding", "checkpointHash", "evidence", "nodeConfiguration", "posture", "request", "resolvedPolicy", "schemaVersion"];
    private static readonly string[] _structuredFieldProperties = ["choices", "fieldId", "kind", "maxTextCharacters", "required"];
    private static readonly string[] _timingProperties = ["expiresAtUtc", "requestedAtUtc"];

    /// <summary>Serializes a validated checkpoint into deterministic compact schema-1 JSON.</summary>
    /// <param name="checkpoint">The checkpoint to serialize.</param>
    /// <param name="json">The canonical JSON when successful.</param>
    /// <param name="validation">The validation failures when serialization is rejected.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerialize(
        GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out string? json,
        out GovernedLoopHumanInputWaitingCheckpointValidationResult validation)
    {
        validation = GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint);
        json = null;
        if (!validation.IsValid)
        {
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCheckpoint(writer, checkpoint!);
            writer.Flush();
        }
        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (json.Length <= GovernedLoopHumanInputWaitingCheckpointContractLimits.MaxJsonCharacters)
        {
            return true;
        }

        json = null;
        validation = Invalid("checkpoint_json_too_large", "$", "Canonical checkpoint JSON exceeds the schema-1 size bound.");
        return false;
    }

    /// <summary>Parses only exact canonical schema-1 checkpoint JSON, rejecting unknown, duplicate, missing, malformed, alternate-order, alternate-lexeme, or forward-version artifacts.</summary>
    /// <param name="json">The untrusted candidate JSON.</param>
    /// <param name="checkpoint">The detached validated checkpoint when successful.</param>
    /// <param name="validation">The structured parse or contract failures.</param>
    /// <returns><see langword="true"/> only when the JSON is valid, restart-stable, and byte-for-byte canonical.</returns>
    public static bool TryDeserialize(
        string? json,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out GovernedLoopHumanInputWaitingCheckpointValidationResult validation)
    {
        checkpoint = null;
        if (string.IsNullOrEmpty(json) || json.Length > GovernedLoopHumanInputWaitingCheckpointContractLimits.MaxJsonCharacters)
        {
            validation = Invalid("invalid_checkpoint_json", "$", "Checkpoint JSON must be non-empty and within the schema-1 size bound.");
            return false;
        }

        var errors = new List<GovernedLoopHumanInputWaitingCheckpointValidationError>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var parsed = ReadCheckpoint(document.RootElement, "$", errors);
            if (errors.Count != 0 || parsed is null)
            {
                validation = new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
                return false;
            }
            if (!GovernedLoopHumanInputWaitingCheckpointContractSnapshot.TryCapture(parsed, out var snapshot, out validation))
            {
                return false;
            }
            if (!TrySerialize(snapshot, out var canonical, out validation))
            {
                return false;
            }
            if (!string.Equals(json, canonical, StringComparison.Ordinal))
            {
                validation = Invalid("noncanonical_checkpoint_json", "$", "Checkpoint JSON must exactly equal its canonical compact schema-1 representation.");
                return false;
            }

            checkpoint = snapshot;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            validation = Invalid("invalid_checkpoint_json", "$", "Checkpoint JSON is malformed or has an invalid schema-1 shape.");
            return false;
        }
    }

    private static void WriteCheckpoint(Utf8JsonWriter writer, GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("binding");
        WriteBinding(writer, checkpoint.Binding);
        writer.WriteString("checkpointHash", checkpoint.CheckpointHash);
        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var evidence in checkpoint.Evidence) WriteEvidence(writer, evidence);
        writer.WriteEndArray();
        writer.WritePropertyName("nodeConfiguration");
        WriteConfiguration(writer, checkpoint.NodeConfiguration);
        writer.WriteNumber("posture", (int)checkpoint.Posture);
        writer.WritePropertyName("request");
        WriteRequest(writer, checkpoint.Request);
        writer.WritePropertyName("resolvedPolicy");
        WriteResolvedPolicy(writer, checkpoint.ResolvedPolicy);
        writer.WriteNumber("schemaVersion", checkpoint.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter writer, GovernedLoopHumanInputWaitingCheckpointBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("admissionReceiptHash", binding.AdmissionReceiptHash);
        writer.WriteNumber("activationOrdinal", binding.ActivationOrdinal);
        writer.WriteString("checkpointId", binding.CheckpointId);
        WriteNullableString(writer, "cycleId", binding.CycleId);
        WriteNullableInt(writer, "cycleIteration", binding.CycleIteration);
        writer.WritePropertyName("execution");
        WriteExecution(writer, binding.Execution);
        writer.WriteString("frontierHash", binding.FrontierHash);
        writer.WriteNumber("frontierVersion", binding.FrontierVersion);
        writer.WriteString("graphArtifactHash", binding.GraphArtifactHash);
        writer.WriteString("graphLayoutHash", binding.GraphLayoutHash);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteNumber("nodeVisitOrdinal", binding.NodeVisitOrdinal);
        writer.WritePropertyName("publication");
        WritePublication(writer, binding.Publication);
        writer.WriteNumber("schemaVersion", binding.SchemaVersion);
        writer.WriteString("workspaceId", binding.WorkspaceId);
        writer.WriteEndObject();
    }

    private static void WriteExecution(Utf8JsonWriter writer, GovernedLoopExecutionBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteNumber("executionGeneration", binding.ExecutionGeneration);
        writer.WritePropertyName("revision");
        WriteRevision(writer, binding.Revision);
        writer.WriteString("runId", binding.RunId);
        writer.WriteNumber("schemaVersion", binding.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WritePublication(Utf8JsonWriter writer, GovernedLoopRevisionPublicationPin publication)
    {
        writer.WriteStartObject();
        writer.WriteString("publicationOperationId", publication.PublicationOperationId);
        writer.WritePropertyName("revision");
        WriteRevision(writer, publication.Revision);
        writer.WriteNumber("schemaVersion", publication.SchemaVersion);
        writer.WriteString("validationEvidenceHash", publication.ValidationEvidenceHash);
        writer.WriteEndObject();
    }

    private static void WriteRevision(Utf8JsonWriter writer, GovernedLoopRevisionReference revision)
    {
        writer.WriteStartObject();
        writer.WriteString("executableHash", revision.ExecutableHash);
        writer.WriteString("graphId", revision.GraphId);
        writer.WriteString("revisionId", revision.RevisionId);
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteConfiguration(Utf8JsonWriter writer, GovernedLoopHumanInputNodeConfiguration configuration)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("eligibleRespondents");
        WriteRespondents(writer, configuration.EligibleRespondents);
        writer.WriteString("failurePolicyReference", configuration.FailurePolicyReference);
        writer.WriteNumber("privacyClass", (int)configuration.PrivacyClass);
        writer.WriteString("prompt", configuration.Prompt);
        writer.WriteString("purpose", configuration.Purpose);
        writer.WriteString("requestSchemaReference", configuration.RequestSchemaReference);
        writer.WritePropertyName("responsePolicy");
        WriteResponsePolicy(writer, configuration.ResponsePolicy);
        writer.WritePropertyName("responseSchema");
        WriteResponseSchema(writer, configuration.ResponseSchema);
        writer.WriteNumber("schemaVersion", configuration.SchemaVersion);
        writer.WriteString("timeoutPolicyReference", configuration.TimeoutPolicyReference);
        writer.WriteEndObject();
    }

    private static void WriteRequest(Utf8JsonWriter writer, HumanInputRequest request)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("binding");
        WriteRequestBinding(writer, request.Binding);
        writer.WritePropertyName("eligibleRespondents");
        WriteRespondents(writer, request.EligibleRespondents);
        writer.WriteNumber("privacyClass", (int)request.PrivacyClass);
        writer.WriteString("prompt", request.Prompt);
        writer.WriteString("purpose", request.Purpose);
        writer.WriteString("requestHash", request.RequestHash);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteString("requestVersionId", request.RequestVersionId);
        writer.WritePropertyName("responsePolicy");
        WriteResponsePolicy(writer, request.ResponsePolicy);
        writer.WritePropertyName("responseSchema");
        WriteResponseSchema(writer, request.ResponseSchema);
        writer.WriteNumber("schemaVersion", request.SchemaVersion);
        writer.WritePropertyName("timing");
        WriteTiming(writer, request.Timing);
        writer.WritePropertyName("continuationBinding");
        WriteContinuationBinding(writer, request.ContinuationBinding);
        writer.WriteEndObject();
    }

    private static void WriteResolvedPolicy(Utf8JsonWriter writer, HumanInputPolicyResolutionSnapshot policy)
    {
        writer.WriteStartObject();
        writer.WriteString("actorId", policy.ActorId);
        WriteTime(writer, "expiresAtUtc", policy.ExpiresAtUtc);
        writer.WritePropertyName("failurePolicy");
        WriteResolvedPolicyArtifact(writer, policy.FailurePolicy);
        writer.WriteString("graphId", policy.GraphId);
        writer.WriteString("graphRevisionId", policy.GraphRevisionId);
        writer.WriteString("nodeId", policy.NodeId);
        WriteTime(writer, "resolvedAtUtc", policy.ResolvedAtUtc);
        writer.WriteString("resolutionHash", policy.ResolutionHash);
        writer.WriteNumber("schemaVersion", policy.SchemaVersion);
        writer.WriteNumber("terminalDisposition", (int)policy.TerminalDisposition);
        writer.WritePropertyName("timeoutPolicy");
        WriteResolvedPolicyArtifact(writer, policy.TimeoutPolicy);
        writer.WriteString("workspaceId", policy.WorkspaceId);
        writer.WriteEndObject();
    }

    private static void WriteResolvedPolicyArtifact(Utf8JsonWriter writer, HumanInputPolicyArtifact policy)
    {
        writer.WriteStartObject();
        writer.WriteString("authorityActorId", policy.AuthorityActorId);
        writer.WriteString("contentHash", policy.ContentHash);
        writer.WriteString("graphId", policy.GraphId);
        writer.WriteNumber("kind", (int)policy.Kind);
        writer.WriteString("policyId", policy.PolicyId);
        if (policy.ResponseWindowMilliseconds is { } window) writer.WriteNumber("responseWindowMilliseconds", window); else writer.WriteNull("responseWindowMilliseconds");
        writer.WriteString("revisionId", policy.RevisionId);
        writer.WriteNumber("schemaVersion", policy.SchemaVersion);
        writer.WriteNumber("terminalDisposition", (int)policy.TerminalDisposition);
        writer.WriteString("workspaceId", policy.WorkspaceId);
        writer.WriteEndObject();
    }

    private static void WriteRequestBinding(Utf8JsonWriter writer, HumanInputRequestBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("checkpointId", binding.CheckpointId);
        writer.WriteString("loopGraphId", binding.LoopGraphId);
        writer.WriteString("loopRevisionId", binding.LoopRevisionId);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteString("runId", binding.RunId);
        writer.WriteString("workspaceId", binding.WorkspaceId);
        writer.WriteEndObject();
    }

    private static void WriteResponseSchema(Utf8JsonWriter writer, HumanInputResponseSchema? schema)
    {
        if (schema is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("choices");
        WriteChoices(writer, schema.Choices);
        writer.WriteNumber("kind", (int)schema.Kind);
        WriteNullableInt(writer, "maxTextCharacters", schema.MaxTextCharacters);
        writer.WritePropertyName("referencePolicy");
        WriteReferencePolicy(writer, schema.ReferencePolicy);
        writer.WritePropertyName("structuredFields");
        WriteStructuredFields(writer, schema.StructuredFields);
        writer.WriteEndObject();
    }

    private static void WriteChoices(Utf8JsonWriter writer, HumanInputChoice[]? choices)
    {
        if (choices is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var choice in choices)
        {
            writer.WriteStartObject();
            writer.WriteString("choiceId", choice?.ChoiceId);
            writer.WriteString("displayText", choice?.DisplayText);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteStructuredFields(Utf8JsonWriter writer, HumanInputStructuredFieldSchema[]? fields)
    {
        if (fields is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var field in fields)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("choices");
            WriteChoices(writer, field?.Choices);
            writer.WriteString("fieldId", field?.FieldId);
            writer.WriteNumber("kind", (int)(field?.Kind ?? HumanInputStructuredFieldKind.Unknown));
            WriteNullableInt(writer, "maxTextCharacters", field?.MaxTextCharacters);
            writer.WriteBoolean("required", field?.Required ?? false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteReferencePolicy(Utf8JsonWriter writer, HumanInputReferencePolicy? policy)
    {
        if (policy is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)policy.Kind);
        WriteNullableInt(writer, "maxReferenceCharacters", policy.MaxReferenceCharacters);
        writer.WriteEndObject();
    }

    private static void WriteRespondents(Utf8JsonWriter writer, IEnumerable<HumanInputEligibleRespondent?>? respondents)
    {
        if (respondents is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var respondent in respondents)
        {
            writer.WriteStartObject();
            writer.WriteString("respondentId", respondent?.RespondentId);
            writer.WriteString("respondentRoleId", respondent?.RespondentRoleId);
            writer.WriteString("routingReference", respondent?.RoutingReference);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteResponsePolicy(Utf8JsonWriter writer, HumanInputResponsePolicy? policy)
    {
        if (policy is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)policy.Kind);
        writer.WritePropertyName("orderedRoleIds");
        if (policy.OrderedRoleIds is not { } roles)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var role in roles) writer.WriteStringValue(role);
            writer.WriteEndArray();
        }
        WriteNullableInt(writer, "requiredResponseCount", policy.RequiredResponseCount);
        writer.WriteEndObject();
    }

    private static void WriteTiming(Utf8JsonWriter writer, HumanInputTiming timing)
    {
        writer.WriteStartObject();
        WriteTime(writer, "expiresAtUtc", timing.ExpiresAtUtc);
        WriteTime(writer, "requestedAtUtc", timing.RequestedAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteContinuationBinding(Utf8JsonWriter writer, HumanInputContinuationBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("checkpointId", binding.CheckpointId);
        writer.WriteNumber("kind", (int)binding.Kind);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, GovernedLoopHumanInputWaitingCheckpointEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("answerSelection");
        WriteSelection(writer, evidence.AnswerSelection);
        writer.WriteString("evidenceHash", evidence.EvidenceHash);
        writer.WriteNumber("kind", (int)evidence.Kind);
        WriteTime(writer, "occurredAtUtc", evidence.OccurredAtUtc);
        writer.WriteString("previousEvidenceHash", evidence.PreviousEvidenceHash);
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteNumber("sequence", evidence.Sequence);
        WriteNullableString(writer, "supersedingCheckpointHash", evidence.SupersedingCheckpointHash);
        WriteNullableString(writer, "supersedingCheckpointId", evidence.SupersedingCheckpointId);
        WriteNullableString(writer, "terminalizationReceiptHash", evidence.TerminalizationReceiptHash);
        WriteNullableString(writer, "terminalizationReceiptId", evidence.TerminalizationReceiptId);
        writer.WriteEndObject();
    }

    private static void WriteSelection(Utf8JsonWriter writer, HumanInputResponseSelectionReference? selection)
    {
        if (selection is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("request");
        WriteRequestReference(writer, selection.Request);
        writer.WriteNumber("schemaVersion", selection.SchemaVersion);
        writer.WriteString("selectionHash", selection.SelectionHash);
        writer.WriteString("selectionId", selection.SelectionId);
        writer.WriteEndObject();
    }

    private static void WriteRequestReference(Utf8JsonWriter writer, HumanInputRequestReference request)
    {
        writer.WriteStartObject();
        writer.WriteString("requestHash", request.RequestHash);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteString("requestVersionId", request.RequestVersionId);
        writer.WriteNumber("schemaVersion", request.SchemaVersion);
        writer.WriteEndObject();
    }

    private static GovernedLoopHumanInputWaitingCheckpoint? ReadCheckpoint(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _stateProperties, errors)) return null;
        return new GovernedLoopHumanInputWaitingCheckpoint(ReadInt(value, "schemaVersion", path, errors), ReadBinding(value.GetProperty("binding"), path + ".binding", errors)!, ReadConfiguration(value.GetProperty("nodeConfiguration"), path + ".nodeConfiguration", errors)!, ReadResolvedPolicy(value.GetProperty("resolvedPolicy"), path + ".resolvedPolicy", errors)!, ReadRequest(value.GetProperty("request"), path + ".request", errors)!, (GovernedLoopHumanInputWaitingCheckpointPosture)ReadInt(value, "posture", path, errors), ReadEvidence(value.GetProperty("evidence"), path + ".evidence", errors), ReadString(value, "checkpointHash", path, errors)!);
    }

    private static HumanInputPolicyResolutionSnapshot? ReadResolvedPolicy(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _resolvedPolicyProperties, errors)) return null;
        return new HumanInputPolicyResolutionSnapshot(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "workspaceId", path, errors)!, ReadString(value, "graphId", path, errors)!, ReadString(value, "graphRevisionId", path, errors)!, ReadString(value, "nodeId", path, errors)!, ReadString(value, "actorId", path, errors)!, ReadResolvedPolicyArtifact(value.GetProperty("timeoutPolicy"), path + ".timeoutPolicy", errors)!, ReadResolvedPolicyArtifact(value.GetProperty("failurePolicy"), path + ".failurePolicy", errors)!, ReadTime(value, "resolvedAtUtc", path, errors), ReadTime(value, "expiresAtUtc", path, errors), (HumanInputTerminalDisposition)ReadInt(value, "terminalDisposition", path, errors), ReadString(value, "resolutionHash", path, errors)!);
    }

    private static HumanInputPolicyArtifact? ReadResolvedPolicyArtifact(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _resolvedPolicyArtifactProperties, errors)) return null;
        return new HumanInputPolicyArtifact(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "policyId", path, errors)!, ReadString(value, "revisionId", path, errors)!, (HumanInputPolicyKind)ReadInt(value, "kind", path, errors), ReadString(value, "workspaceId", path, errors)!, ReadString(value, "graphId", path, errors)!, ReadString(value, "authorityActorId", path, errors)!, ReadNullableLong(value, "responseWindowMilliseconds", path, errors), (HumanInputTerminalDisposition)ReadInt(value, "terminalDisposition", path, errors), ReadString(value, "contentHash", path, errors)!);
    }

    private static GovernedLoopHumanInputWaitingCheckpointBinding? ReadBinding(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _bindingProperties, errors)) return null;
        return new GovernedLoopHumanInputWaitingCheckpointBinding(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "workspaceId", path, errors)!, ReadExecution(value.GetProperty("execution"), path + ".execution", errors)!, ReadPublication(value.GetProperty("publication"), path + ".publication", errors)!, ReadString(value, "graphArtifactHash", path, errors)!, ReadString(value, "graphLayoutHash", path, errors)!, ReadString(value, "admissionReceiptHash", path, errors)!, ReadLong(value, "frontierVersion", path, errors), ReadString(value, "frontierHash", path, errors)!, ReadInt(value, "activationOrdinal", path, errors), ReadNullableString(value, "cycleId", path, errors), ReadNullableInt(value, "cycleIteration", path, errors), ReadString(value, "nodeId", path, errors)!, ReadInt(value, "nodeVisitOrdinal", path, errors), ReadString(value, "checkpointId", path, errors)!);
    }

    private static GovernedLoopExecutionBinding? ReadExecution(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _executionProperties, errors)) return null;
        var revision = ReadRevision(value.GetProperty("revision"), path + ".revision", errors);
        try { return revision is null ? null : GovernedLoopExecutionBinding.Create(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "runId", path, errors)!, revision, ReadLong(value, "executionGeneration", path, errors)); }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException) { Add(errors, "invalid_execution", path, "Execution binding is not a valid schema-1 coordinate."); return null; }
    }

    private static GovernedLoopRevisionPublicationPin? ReadPublication(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _publicationProperties, errors)) return null;
        return new GovernedLoopRevisionPublicationPin(ReadInt(value, "schemaVersion", path, errors), ReadRevision(value.GetProperty("revision"), path + ".revision", errors)!, ReadString(value, "publicationOperationId", path, errors)!, ReadString(value, "validationEvidenceHash", path, errors)!);
    }

    private static GovernedLoopRevisionReference? ReadRevision(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _revisionProperties, errors)) return null;
        try { return GovernedLoopRevisionReference.Create(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "graphId", path, errors)!, ReadString(value, "revisionId", path, errors)!, ReadString(value, "executableHash", path, errors)!); }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException) { Add(errors, "invalid_revision", path, "Revision reference is not a valid schema-1 coordinate."); return null; }
    }

    private static GovernedLoopHumanInputNodeConfiguration? ReadConfiguration(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _configProperties, errors)) return null;
        return new GovernedLoopHumanInputNodeConfiguration(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "requestSchemaReference", path, errors), ReadString(value, "purpose", path, errors), ReadString(value, "prompt", path, errors), ReadResponseSchema(value.GetProperty("responseSchema"), path + ".responseSchema", errors), (HumanInputPrivacyClass)ReadInt(value, "privacyClass", path, errors), ReadRespondents(value.GetProperty("eligibleRespondents"), path + ".eligibleRespondents", errors), ReadResponsePolicy(value.GetProperty("responsePolicy"), path + ".responsePolicy", errors), ReadString(value, "timeoutPolicyReference", path, errors), ReadString(value, "failurePolicyReference", path, errors));
    }

    private static HumanInputRequest? ReadRequest(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _requestProperties, errors)) return null;
        return new HumanInputRequest(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "requestId", path, errors)!, ReadString(value, "requestVersionId", path, errors)!, ReadRequestBinding(value.GetProperty("binding"), path + ".binding", errors)!, ReadString(value, "purpose", path, errors)!, ReadString(value, "prompt", path, errors)!, ReadResponseSchema(value.GetProperty("responseSchema"), path + ".responseSchema", errors)!, (HumanInputPrivacyClass)ReadInt(value, "privacyClass", path, errors), ReadRespondents(value.GetProperty("eligibleRespondents"), path + ".eligibleRespondents", errors)!.Cast<HumanInputEligibleRespondent>().ToArray(), ReadTiming(value.GetProperty("timing"), path + ".timing", errors)!, ReadResponsePolicy(value.GetProperty("responsePolicy"), path + ".responsePolicy", errors)!, ReadContinuationBinding(value.GetProperty("continuationBinding"), path + ".continuationBinding", errors)!, ReadString(value, "requestHash", path, errors)!);
    }

    private static HumanInputRequestBinding? ReadRequestBinding(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _requestBindingProperties, errors)) return null;
        return new HumanInputRequestBinding(ReadString(value, "workspaceId", path, errors)!, ReadString(value, "loopGraphId", path, errors)!, ReadString(value, "loopRevisionId", path, errors)!, ReadString(value, "nodeId", path, errors)!, ReadString(value, "runId", path, errors)!, ReadString(value, "checkpointId", path, errors)!);
    }

    private static HumanInputResponseSchema? ReadResponseSchema(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _responseSchemaProperties, errors)) return null;
        return new HumanInputResponseSchema((HumanInputResponseKind)ReadInt(value, "kind", path, errors), ReadNullableInt(value, "maxTextCharacters", path, errors), ReadChoices(value.GetProperty("choices"), path + ".choices", errors), ReadStructuredFields(value.GetProperty("structuredFields"), path + ".structuredFields", errors), ReadReferencePolicy(value.GetProperty("referencePolicy"), path + ".referencePolicy", errors));
    }

    private static HumanInputChoice[]? ReadChoices(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array or null is required."); return null; }
        var values = new List<HumanInputChoice>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (Shape(item, $"{path}[{index}]", _choiceProperties, errors)) values.Add(new HumanInputChoice(ReadString(item, "choiceId", $"{path}[{index}]", errors)!, ReadString(item, "displayText", $"{path}[{index}]", errors)!));
            index++;
        }
        return values.ToArray();
    }

    private static HumanInputStructuredFieldSchema[]? ReadStructuredFields(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array or null is required."); return null; }
        var values = new List<HumanInputStructuredFieldSchema>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (Shape(item, $"{path}[{index}]", _structuredFieldProperties, errors)) values.Add(new HumanInputStructuredFieldSchema(ReadString(item, "fieldId", $"{path}[{index}]", errors)!, (HumanInputStructuredFieldKind)ReadInt(item, "kind", $"{path}[{index}]", errors), ReadBoolean(item, "required", $"{path}[{index}]", errors), ReadNullableInt(item, "maxTextCharacters", $"{path}[{index}]", errors), ReadChoices(item.GetProperty("choices"), $"{path}[{index}].choices", errors)));
            index++;
        }
        return values.ToArray();
    }

    private static HumanInputReferencePolicy? ReadReferencePolicy(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!Shape(value, path, _referencePolicyProperties, errors)) return null;
        return new HumanInputReferencePolicy((HumanInputReferenceKind)ReadInt(value, "kind", path, errors), ReadInt(value, "maxReferenceCharacters", path, errors));
    }

    private static IReadOnlyList<HumanInputEligibleRespondent?>? ReadRespondents(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array or null is required."); return null; }
        var values = new List<HumanInputEligibleRespondent>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (Shape(item, $"{path}[{index}]", _respondentProperties, errors)) values.Add(new HumanInputEligibleRespondent(ReadString(item, "respondentId", $"{path}[{index}]", errors)!, ReadString(item, "respondentRoleId", $"{path}[{index}]", errors)!, ReadString(item, "routingReference", $"{path}[{index}]", errors)!));
            index++;
        }
        return values;
    }

    private static HumanInputResponsePolicy? ReadResponsePolicy(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _policyProperties, errors)) return null;
        var roles = ReadRoles(value.GetProperty("orderedRoleIds"), path + ".orderedRoleIds", errors);
        return new HumanInputResponsePolicy((HumanInputResponsePolicyKind)ReadInt(value, "kind", path, errors), ReadNullableInt(value, "requiredResponseCount", path, errors), roles);
    }

    private static ImmutableArray<string>? ReadRoles(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array or null is required."); return null; }
        var values = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray()) { if (item.ValueKind == JsonValueKind.String) values.Add(item.GetString()!); else Add(errors, "invalid_json_type", $"{path}[{index}]", "A JSON string is required."); index++; }
        return values.ToImmutable();
    }

    private static HumanInputTiming? ReadTiming(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _timingProperties, errors)) return null;
        return new HumanInputTiming(ReadTime(value, "requestedAtUtc", path, errors), ReadTime(value, "expiresAtUtc", path, errors));
    }

    private static HumanInputContinuationBinding? ReadContinuationBinding(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _continuationProperties, errors)) return null;
        return new HumanInputContinuationBinding((HumanInputContinuationPolicyKind)ReadInt(value, "kind", path, errors), ReadString(value, "nodeId", path, errors)!, ReadString(value, "checkpointId", path, errors)!);
    }

    private static ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> ReadEvidence(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array is required."); return default; }
        var values = ImmutableArray.CreateBuilder<GovernedLoopHumanInputWaitingCheckpointEvidence>();
        var index = 0;
        foreach (var item in value.EnumerateArray()) { var evidence = ReadEvidenceItem(item, $"{path}[{index}]", errors); if (evidence is not null) values.Add(evidence); index++; }
        return values.ToImmutable();
    }

    private static GovernedLoopHumanInputWaitingCheckpointEvidence? ReadEvidenceItem(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _evidenceProperties, errors)) return null;
        return new GovernedLoopHumanInputWaitingCheckpointEvidence(ReadInt(value, "schemaVersion", path, errors), ReadLong(value, "sequence", path, errors), (GovernedLoopHumanInputWaitingCheckpointEvidenceKind)ReadInt(value, "kind", path, errors), ReadTime(value, "occurredAtUtc", path, errors), ReadSelection(value.GetProperty("answerSelection"), path + ".answerSelection", errors), ReadNullableString(value, "supersedingCheckpointId", path, errors), ReadNullableString(value, "supersedingCheckpointHash", path, errors), ReadNullableString(value, "terminalizationReceiptId", path, errors), ReadNullableString(value, "terminalizationReceiptHash", path, errors), ReadString(value, "previousEvidenceHash", path, errors)!, ReadString(value, "evidenceHash", path, errors)!);
    }

    private static HumanInputResponseSelectionReference? ReadSelection(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!Shape(value, path, _selectionProperties, errors)) return null;
        return new HumanInputResponseSelectionReference(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "selectionId", path, errors)!, ReadRequestReference(value.GetProperty("request"), path + ".request", errors)!, ReadString(value, "selectionHash", path, errors)!);
    }

    private static HumanInputRequestReference? ReadRequestReference(JsonElement value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Shape(value, path, _requestReferenceProperties, errors)) return null;
        return new HumanInputRequestReference(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "requestId", path, errors)!, ReadString(value, "requestVersionId", path, errors)!, ReadString(value, "requestHash", path, errors)!);
    }

    private static bool Shape(JsonElement value, string path, IReadOnlyCollection<string> expected, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object) { Add(errors, "invalid_json_type", path, "A JSON object is required."); return false; }
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length || !names.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            Add(errors, "invalid_json_shape", path, "JSON object contains unknown, duplicate, or missing schema-1 properties.");
            return false;
        }
        return true;
    }

    private static string? ReadString(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) { Add(errors, "invalid_json_type", path + "." + name, "A JSON string is required."); return null; }
        return property.GetString();
    }

    private static string? ReadNullableString(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) { Add(errors, "invalid_json_type", path + "." + name, "A JSON string or null is required."); return null; }
        return property.GetString();
    }

    private static int ReadInt(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result)) { Add(errors, "invalid_json_type", path + "." + name, "A JSON integer is required."); return 0; }
        return result;
    }

    private static int? ReadNullableInt(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result)) { Add(errors, "invalid_json_type", path + "." + name, "A JSON integer or null is required."); return null; }
        return result;
    }

    private static long ReadLong(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result)) { Add(errors, "invalid_json_type", path + "." + name, "A JSON integer is required."); return 0; }
        return result;
    }

    private static long? ReadNullableLong(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result))
        {
            Add(errors, "invalid_json_type", path + "." + name, "A JSON integer or null is required.");
            return null;
        }

        return result;
    }

    private static bool ReadBoolean(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False) { Add(errors, "invalid_json_type", path + "." + name, "A JSON Boolean is required."); return false; }
        return property.GetBoolean();
    }

    private static DateTimeOffset ReadTime(JsonElement value, string name, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        var text = ReadString(value, name, path, errors);
        if (text is null || !DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) { Add(errors, "invalid_timestamp", path + "." + name, "An exact round-trip UTC timestamp is required."); return default; }
        return result;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static void WriteNullableInt(Utf8JsonWriter writer, string name, int? value) { if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value); }
    private static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset value) => writer.WriteString(name, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static void Add(List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors, string code, string path, string message) => errors.Add(new GovernedLoopHumanInputWaitingCheckpointValidationError(code, path, message));
    private static GovernedLoopHumanInputWaitingCheckpointValidationResult Invalid(string code, string path, string message) => new([new GovernedLoopHumanInputWaitingCheckpointValidationError(code, path, message)]);
}
