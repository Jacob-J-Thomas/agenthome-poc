using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Computes the canonical hash binding a workspace-global operation identity to exact Human Input lifecycle intent.</summary>
public static class HumanInputRequestLifecycleCommandHash
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Computes a lowercase SHA-256 digest over every command field except the supplied command hash.</summary>
    /// <param name="command">The exact command to snapshot and hash.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the command is incomplete, exceeds a schema-1 bound, contains malformed UTF-16, or carries an invalid candidate request.</exception>
    public static string Compute(HumanInputRequestLifecycleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var candidate = CaptureCandidate(command.CandidateRequest);
        RequireBounded(command);

        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", command.SchemaVersion);
            writer.WriteString("operationId", command.OperationId);
            writer.WriteNumber("kind", (int)command.Kind);
            writer.WriteString("requestId", command.RequestId);
            writer.WriteNumber("expectedLifecycleVersion", command.ExpectedLifecycleVersion);
            writer.WriteNumber("expectedLifecycleStatus", (int)command.ExpectedLifecycleStatus);
            WriteRequestReference(writer, "expectedRequest", command.ExpectedRequest);
            WriteBinding(writer, "expectedBinding", command.ExpectedBinding);
            WriteCandidateRequest(writer, candidate);
            WriteGrantReference(writer, command.GrantReference);
            writer.WriteString("reason", command.Reason.Value);
            writer.WriteEndObject();
            writer.Flush();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Human Input lifecycle command text must contain well-formed UTF-16.", nameof(command), exception);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns an immutable command copy carrying its canonical exact-intent hash.</summary>
    /// <param name="command">The exact command to hash.</param>
    /// <returns>A command copy with its canonical hash.</returns>
    public static HumanInputRequestLifecycleCommand Apply(HumanInputRequestLifecycleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command with { RequestHash = Compute(command) };
    }

    /// <summary>Determines whether a command retains its exact canonical hash.</summary>
    /// <param name="command">The command to inspect.</param>
    /// <returns><see langword="true"/> only when the supplied hash is canonical and fixed-time equal to the recomputed hash.</returns>
    public static bool Matches(HumanInputRequestLifecycleCommand? command)
    {
        if (command is null || !IsSha256(command.RequestHash))
        {
            return false;
        }

        try
        {
            var expected = Encoding.ASCII.GetBytes(Compute(command));
            var actual = Encoding.ASCII.GetBytes(command.RequestHash);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static HumanInputRequest? CaptureCandidate(HumanInputRequest? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        if (!HumanInputRequestSnapshot.TryCapture(candidate, out var snapshot, out _) || snapshot is null)
        {
            throw new ArgumentException("The Human Input lifecycle candidate must be a bounded valid immutable request.", nameof(candidate));
        }

        return snapshot;
    }

    private static void RequireBounded(HumanInputRequestLifecycleCommand command)
    {
        if (!IsWithin(command.OperationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters)
            || !IsWithin(command.RequestId, HumanInputLimits.MaxIdentifierCharacters)
            || command.Reason is null
            || !IsWithin(command.Reason.Value, AuthorityContractLimits.MaxPurposeCharacters)
            || !IsWithin(command.RequestHash, HumanInputRequestLifecycleContractLimits.Sha256HexCharacters))
        {
            throw new ArgumentException("Human Input lifecycle command text is incomplete or exceeds schema-1 bounds.", nameof(command));
        }

        if (command.ExpectedRequest is { } expected
            && (!IsWithin(expected.RequestId, HumanInputLimits.MaxIdentifierCharacters)
                || !IsWithin(expected.RequestVersionId, HumanInputLimits.MaxIdentifierCharacters)
                || !IsWithin(expected.RequestHash, HumanInputRequestLifecycleContractLimits.Sha256HexCharacters)))
        {
            throw new ArgumentException("The expected Human Input request reference exceeds schema-1 bounds.", nameof(command));
        }

        if (command.ExpectedBinding is { } binding
            && !BindingIsWithinBounds(binding))
        {
            throw new ArgumentException("The expected Human Input request binding is incomplete or exceeds schema-1 bounds.", nameof(command));
        }

        if (command.GrantReference is { } grant
            && (grant.GrantId is null
                || grant.Revision is null
                || !IsWithin(grant.GrantId.Value, AuthorityGrantContractLimits.MaxGrantIdCharacters)
                || !IsWithin(grant.ContentHash, 7 + HumanInputRequestLifecycleContractLimits.Sha256HexCharacters)))
        {
            throw new ArgumentException("The exact authority-grant reference is incomplete or exceeds schema-1 bounds.", nameof(command));
        }
    }

    private static void WriteRequestReference(Utf8JsonWriter writer, string propertyName, HumanInputRequestReference? reference)
    {
        writer.WritePropertyName(propertyName);
        if (reference is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", reference.SchemaVersion);
        writer.WriteString("requestId", reference.RequestId);
        writer.WriteString("requestVersionId", reference.RequestVersionId);
        writer.WriteString("requestHash", reference.RequestHash);
        writer.WriteEndObject();
    }

    private static void WriteCandidateRequest(Utf8JsonWriter writer, HumanInputRequest? request)
    {
        writer.WritePropertyName("candidateRequest");
        if (request is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", request.SchemaVersion);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteString("requestVersionId", request.RequestVersionId);
        WriteBinding(writer, "binding", request.Binding);
        writer.WriteString("purpose", request.Purpose);
        writer.WriteString("prompt", request.Prompt);
        WriteResponseSchema(writer, request.ResponseSchema);
        writer.WriteNumber("privacyClass", (int)request.PrivacyClass);
        writer.WriteStartArray("eligibleRespondents");
        foreach (var respondent in request.EligibleRespondents)
        {
            writer.WriteStartObject();
            writer.WriteString("respondentId", respondent.RespondentId);
            writer.WriteString("routingReference", respondent.RoutingReference);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("timing");
        writer.WriteString("requestedAtUtc", request.Timing.RequestedAtUtc);
        writer.WriteString("expiresAtUtc", request.Timing.ExpiresAtUtc);
        writer.WriteEndObject();
        writer.WriteNumber("responsePolicy", (int)request.ResponsePolicy.Kind);
        writer.WriteStartObject("continuationBinding");
        writer.WriteNumber("kind", (int)request.ContinuationBinding.Kind);
        writer.WriteString("nodeId", request.ContinuationBinding.NodeId);
        writer.WriteString("checkpointId", request.ContinuationBinding.CheckpointId);
        writer.WriteEndObject();
        writer.WriteString("requestHash", request.RequestHash);
        writer.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter writer, string propertyName, HumanInputRequestBinding? binding)
    {
        writer.WritePropertyName(propertyName);
        if (binding is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("workspaceId", binding.WorkspaceId);
        writer.WriteString("loopGraphId", binding.LoopGraphId);
        writer.WriteString("loopRevisionId", binding.LoopRevisionId);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteString("runId", binding.RunId);
        writer.WriteString("checkpointId", binding.CheckpointId);
        writer.WriteEndObject();
    }

    private static bool BindingIsWithinBounds(HumanInputRequestBinding binding)
        => IsWithin(binding.WorkspaceId, HumanInputLimits.MaxIdentifierCharacters)
            && IsWithin(binding.LoopGraphId, HumanInputLimits.MaxIdentifierCharacters)
            && IsWithin(binding.LoopRevisionId, HumanInputLimits.MaxIdentifierCharacters)
            && IsWithin(binding.NodeId, HumanInputLimits.MaxIdentifierCharacters)
            && IsWithin(binding.RunId, HumanInputLimits.MaxIdentifierCharacters)
            && IsWithin(binding.CheckpointId, HumanInputLimits.MaxIdentifierCharacters);

    private static void WriteResponseSchema(Utf8JsonWriter writer, HumanInputResponseSchema schema)
    {
        writer.WriteStartObject("responseSchema");
        writer.WriteNumber("kind", (int)schema.Kind);
        WriteNullableNumber(writer, "maxTextCharacters", schema.MaxTextCharacters);
        WriteChoices(writer, "choices", schema.Choices);
        writer.WritePropertyName("structuredFields");
        if (schema.StructuredFields is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var field in schema.StructuredFields)
            {
                writer.WriteStartObject();
                writer.WriteString("fieldId", field.FieldId);
                writer.WriteNumber("kind", (int)field.Kind);
                writer.WriteBoolean("required", field.Required);
                WriteNullableNumber(writer, "maxTextCharacters", field.MaxTextCharacters);
                WriteChoices(writer, "choices", field.Choices);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WritePropertyName("referencePolicy");
        if (schema.ReferencePolicy is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)schema.ReferencePolicy.Kind);
            writer.WriteNumber("maxReferenceCharacters", schema.ReferencePolicy.MaxReferenceCharacters);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteChoices(Utf8JsonWriter writer, string propertyName, HumanInputChoice[]? choices)
    {
        writer.WritePropertyName(propertyName);
        if (choices is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var choice in choices)
        {
            writer.WriteStartObject();
            writer.WriteString("choiceId", choice.ChoiceId);
            writer.WriteString("displayText", choice.DisplayText);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteGrantReference(Utf8JsonWriter writer, EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference? reference)
    {
        writer.WritePropertyName("grantReference");
        if (reference is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("grantId", reference.GrantId.Value);
        writer.WriteNumber("revision", reference.Revision.Value);
        writer.WriteString("contentHash", reference.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static bool IsWithin(string? value, int maximum)
    {
        if (value is null || value.Length > maximum)
        {
            return false;
        }

        try
        {
            _ = _strictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: HumanInputRequestLifecycleContractLimits.Sha256HexCharacters }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
