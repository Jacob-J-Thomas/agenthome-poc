using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Computes, applies, and verifies the canonical hash for immutable human-input request contracts.
/// </summary>
public static class HumanInputRequestHash
{
    /// <summary>
    /// Computes a lowercase SHA-256 digest over every behavior-affecting request field in deterministic property and array order.
    /// </summary>
    /// <param name="request">The request to serialize canonically.</param>
    /// <returns>A 64-character lowercase hexadecimal digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when a request value or collection exceeds its declared maximum.</exception>
    public static string Compute(HumanInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedForCanonicalization(request))
        {
            throw new ArgumentException("Human-input request values or collections exceed canonicalization limits.", nameof(request));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            WriteString(writer, "requestId", request.RequestId);
            WriteString(writer, "requestVersionId", request.RequestVersionId);
            writer.WritePropertyName("binding");
            WriteBinding(writer, request.Binding);
            WriteString(writer, "purpose", request.Purpose);
            WriteString(writer, "prompt", request.Prompt);
            writer.WritePropertyName("responseSchema");
            WriteSchema(writer, request.ResponseSchema);
            writer.WriteNumber("privacyClass", (int)request.PrivacyClass);
            writer.WritePropertyName("eligibleRespondents");
            writer.WriteStartArray();
            if (request.EligibleRespondents is not null)
            {
                foreach (var respondent in request.EligibleRespondents)
                {
                    WriteRespondent(writer, respondent);
                }
            }

            writer.WriteEndArray();
            writer.WritePropertyName("timing");
            WriteTiming(writer, request.Timing);
            writer.WritePropertyName("responsePolicy");
            writer.WriteNumberValue((int)(request.ResponsePolicy?.Kind ?? HumanInputResponsePolicyKind.Unknown));
            writer.WritePropertyName("continuationBinding");
            WriteContinuationBinding(writer, request.ContinuationBinding);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a request copy with its canonical hash applied.
    /// </summary>
    /// <param name="request">The request to hash.</param>
    /// <returns>A copy with <see cref="HumanInputRequest.RequestHash"/> set to <see cref="Compute"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when a request value or collection exceeds its declared maximum.</exception>
    public static HumanInputRequest Apply(HumanInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with { RequestHash = Compute(request) };
    }

    /// <summary>
    /// Determines whether a request retains its exact canonical hash.
    /// </summary>
    /// <param name="request">The request to verify.</param>
    /// <returns><see langword="true"/> when the stored hash has equal length and fixed-time equality with the recomputed hash; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when a request value or collection exceeds its declared maximum.</exception>
    public static bool Matches(HumanInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = Encoding.ASCII.GetBytes(Compute(request));
        var actual = Encoding.ASCII.GetBytes(request.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    internal static bool IsBoundedForCanonicalization(HumanInputRequest request)
    {
        if (!IsWithin(request.RequestId, HumanInputLimits.MaxIdentifierCharacters)
            || !IsWithin(request.RequestVersionId, HumanInputLimits.MaxIdentifierCharacters)
            || !IsWithin(request.Purpose, HumanInputLimits.MaxPurposeCharacters)
            || !IsWithin(request.Prompt, HumanInputLimits.MaxPromptCharacters)
            || !IsBoundedBinding(request.Binding)
            || !IsBoundedContinuation(request.ContinuationBinding))
        {
            return false;
        }

        if (request.EligibleRespondents is { Length: > HumanInputLimits.MaxEligibleRespondents })
        {
            return false;
        }

        if (request.EligibleRespondents is { } respondents)
        {
            for (var index = 0; index < respondents.Length; index++)
            {
                if (respondents[index] is { } respondent
                    && (!IsWithin(respondent.RespondentId, HumanInputLimits.MaxIdentifierCharacters)
                        || !IsWithin(respondent.RoutingReference, HumanInputLimits.MaxRoutingReferenceCharacters)))
                {
                    return false;
                }
            }
        }

        var schema = request.ResponseSchema;
        if (!IsOptionalMaximumBounded(schema?.MaxTextCharacters, HumanInputLimits.MaxResponseTextCharacters)
            || !IsOptionalMaximumBounded(schema?.ReferencePolicy?.MaxReferenceCharacters, HumanInputLimits.MaxReferenceCharacters)
            || schema?.Choices is { Length: > HumanInputLimits.MaxChoices }
            || schema?.StructuredFields is { Length: > HumanInputLimits.MaxStructuredFields })
        {
            return false;
        }

        if (schema?.StructuredFields is not { } fields)
        {
            return AreChoicesBounded(schema?.Choices);
        }

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field is not null
                && (!IsWithin(field.FieldId, HumanInputLimits.MaxIdentifierCharacters)
                    || !IsOptionalMaximumBounded(field.MaxTextCharacters, HumanInputLimits.MaxResponseTextCharacters)
                    || !AreChoicesBounded(field.Choices)))
            {
                return false;
            }
        }

        return AreChoicesBounded(schema.Choices);
    }

    private static bool AreChoicesBounded(HumanInputChoice[]? choices)
    {
        if (choices is { Length: > HumanInputLimits.MaxChoices })
        {
            return false;
        }

        if (choices is null)
        {
            return true;
        }

        for (var index = 0; index < choices.Length; index++)
        {
            if (choices[index] is { } choice
                && (!IsWithin(choice.ChoiceId, HumanInputLimits.MaxIdentifierCharacters)
                    || !IsWithin(choice.DisplayText, HumanInputLimits.MaxChoiceDisplayCharacters)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBoundedBinding(HumanInputRequestBinding? binding)
    {
        return binding is null
            || IsWithin(binding.WorkspaceId, HumanInputLimits.MaxIdentifierCharacters)
                && IsWithin(binding.LoopRevisionId, HumanInputLimits.MaxIdentifierCharacters)
                && IsWithin(binding.NodeId, HumanInputLimits.MaxIdentifierCharacters)
                && IsWithin(binding.RunId, HumanInputLimits.MaxIdentifierCharacters)
                && IsWithin(binding.CheckpointId, HumanInputLimits.MaxIdentifierCharacters);
    }

    private static bool IsBoundedContinuation(HumanInputContinuationBinding? binding)
    {
        return binding is null
            || IsWithin(binding.NodeId, HumanInputLimits.MaxIdentifierCharacters)
                && IsWithin(binding.CheckpointId, HumanInputLimits.MaxIdentifierCharacters);
    }

    private static bool IsWithin(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool IsOptionalMaximumBounded(int? value, int maximum) => value is null || value is >= 1 && value <= maximum;

    private static void WriteBinding(Utf8JsonWriter writer, HumanInputRequestBinding? binding)
    {
        if (binding is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "workspaceId", binding.WorkspaceId);
        WriteString(writer, "loopRevisionId", binding.LoopRevisionId);
        WriteString(writer, "nodeId", binding.NodeId);
        WriteString(writer, "runId", binding.RunId);
        WriteString(writer, "checkpointId", binding.CheckpointId);
        writer.WriteEndObject();
    }

    private static void WriteSchema(Utf8JsonWriter writer, HumanInputResponseSchema? schema)
    {
        if (schema is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)schema.Kind);
        if (schema.MaxTextCharacters is { } maximum)
        {
            writer.WriteNumber("maxTextCharacters", maximum);
        }
        else
        {
            writer.WriteNull("maxTextCharacters");
        }

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
                if (field is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                writer.WriteStartObject();
                WriteString(writer, "fieldId", field.FieldId);
                writer.WriteNumber("kind", (int)field.Kind);
                writer.WriteBoolean("required", field.Required);
                if (field.MaxTextCharacters is { } fieldMaximum)
                {
                    writer.WriteNumber("maxTextCharacters", fieldMaximum);
                }
                else
                {
                    writer.WriteNull("maxTextCharacters");
                }

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
            if (choice is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            WriteString(writer, "choiceId", choice.ChoiceId);
            WriteString(writer, "displayText", choice.DisplayText);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRespondent(Utf8JsonWriter writer, HumanInputEligibleRespondent? respondent)
    {
        if (respondent is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "respondentId", respondent.RespondentId);
        WriteString(writer, "routingReference", respondent.RoutingReference);
        writer.WriteEndObject();
    }

    private static void WriteTiming(Utf8JsonWriter writer, HumanInputTiming? timing)
    {
        if (timing is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("requestedAtUtc", timing.RequestedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("expiresAtUtc", timing.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteContinuationBinding(Utf8JsonWriter writer, HumanInputContinuationBinding? binding)
    {
        if (binding is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)binding.Kind);
        WriteString(writer, "nodeId", binding.NodeId);
        WriteString(writer, "checkpointId", binding.CheckpointId);
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
