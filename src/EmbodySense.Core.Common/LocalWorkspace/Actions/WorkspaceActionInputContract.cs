using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Validates and canonically encodes the closed schema-1 workspace action semantic input.</summary>
public static class WorkspaceActionInputContract
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Parses canonical structured input for exactly the operation selected by server registration.</summary>
    public static bool TryParse(
        string? canonicalJson,
        WorkspaceActionKind expectedKind,
        out WorkspaceActionInput? input,
        out string? reasonCode)
    {
        input = null;
        reasonCode = "workspace-input-invalid";
        if (expectedKind is not (WorkspaceActionKind.Append or WorkspaceActionKind.Write or WorkspaceActionKind.Delete)
            || string.IsNullOrEmpty(canonicalJson)
            || Encoding.UTF8.GetByteCount(canonicalJson) > Loops.Execution.Effects.GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, "precondition", "schemaVersion", "scopeId", "segments", "target"))
            {
                reasonCode = "workspace-input-shape-invalid";
                return false;
            }
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var schema)
                || schema != WorkspaceActionContractLimits.CurrentSchemaVersion
                || !root.TryGetProperty("scopeId", out var scopeValue)
                || scopeValue.ValueKind != JsonValueKind.String
                || !WorkspaceActionScopeId.TryParse(ReadString(scopeValue), out var scope)
                || !root.TryGetProperty("target", out var targetValue)
                || targetValue.ValueKind != JsonValueKind.String
                || !WorkspaceRelativeFileTarget.TryParse(ReadString(targetValue), out var target, out reasonCode)
                || !root.TryGetProperty("precondition", out var preconditionValue)
                || !TryReadPrecondition(preconditionValue, out var precondition, out reasonCode)
                || !root.TryGetProperty("segments", out var segmentValues)
                || !TryReadSegments(segmentValues, out var segments, out reasonCode))
            {
                return false;
            }

            if (expectedKind == WorkspaceActionKind.Delete && (segments!.Count != 0 || precondition!.Kind == WorkspaceActionPreconditionKind.ExpectedAbsent)
                || expectedKind is WorkspaceActionKind.Append or WorkspaceActionKind.Write && segments!.Count == 0)
            {
                reasonCode = "workspace-input-operation-mismatch";
                return false;
            }

            input = new WorkspaceActionInput(schema, expectedKind, scope!, target!, precondition!, segments!);
            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            reasonCode = "workspace-input-malformed";
            return false;
        }
    }

    /// <summary>Encodes one validated workspace semantic input as compact deterministic JSON.</summary>
    public static string Encode(WorkspaceActionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var operation = WorkspaceActionOperationIds.For(input.Kind);
        if (!TryParse(EncodeUnchecked(input), input.Kind, out var validated, out var reasonCode)
            || !string.Equals(operation, WorkspaceActionOperationIds.For(validated!.Kind), StringComparison.Ordinal))
        {
            throw new ArgumentException(reasonCode ?? "The workspace input is invalid.", nameof(input));
        }
        return EncodeUnchecked(validated);
    }

    /// <summary>Computes the domain-separated exact optimistic-precondition fingerprint.</summary>
    public static string ComputePreconditionHash(WorkspaceActionPrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        if (!ValidatePrecondition(precondition))
        {
            throw new ArgumentException("The workspace precondition is invalid.", nameof(precondition));
        }
        return WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-action-precondition.v1",
            ((int)precondition.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            precondition.ExpectedContentHash,
            precondition.ExpectedGovernedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            precondition.PriorAfterEvidenceId,
            precondition.PriorAfterEvidenceHash);
    }

    /// <summary>Returns whether the input contains any value-free credential references that require the later trusted host bridge.</summary>
    public static bool RequiresCredentialBridge(WorkspaceActionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Segments.Any(segment => segment.Kind == WorkspaceActionContentSegmentKind.CredentialReference);
    }

    /// <summary>Materializes exact strict-UTF-8 literal bytes and refuses credential references.</summary>
    public static byte[] MaterializeLiteralBytes(WorkspaceActionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (RequiresCredentialBridge(input))
        {
            throw new InvalidOperationException("Credential-reference segments require the shared trusted actuator host bridge.");
        }

        using var buffer = new MemoryStream();
        foreach (var segment in input.Segments)
        {
            var bytes = _strictUtf8.GetBytes(segment.Literal!);
            if (buffer.Length + bytes.Length > WorkspaceActionContractLimits.MaxLiteralUtf8Bytes)
            {
                throw new InvalidOperationException("Workspace literal content exceeds its schema-1 byte limit.");
            }
            buffer.Write(bytes);
        }
        return buffer.ToArray();
    }

    private static bool TryReadPrecondition(JsonElement value, out WorkspaceActionPrecondition? precondition, out string? reasonCode)
    {
        precondition = null;
        reasonCode = "workspace-precondition-invalid";
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("kind", out var kindValue)
            || kindValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var kind = ReadString(kindValue) switch
        {
            "expectedAbsent" => WorkspaceActionPreconditionKind.ExpectedAbsent,
            "expectedContentHash" => WorkspaceActionPreconditionKind.ExpectedContentHash,
            "expectedGovernedVersion" => WorkspaceActionPreconditionKind.ExpectedGovernedVersion,
            _ => WorkspaceActionPreconditionKind.Unknown,
        };
        if (kind == WorkspaceActionPreconditionKind.Unknown)
        {
            return false;
        }

        var expectedProperties = kind switch
        {
            WorkspaceActionPreconditionKind.ExpectedAbsent => new[] { "kind" },
            WorkspaceActionPreconditionKind.ExpectedContentHash => ["expectedContentHash", "kind"],
            WorkspaceActionPreconditionKind.ExpectedGovernedVersion => ["expectedGovernedVersion", "kind", "priorAfterEvidenceHash", "priorAfterEvidenceId"],
            _ => [],
        };
        if (!HasExactProperties(value, expectedProperties))
        {
            return false;
        }

        string? expectedHash = null;
        long? version = null;
        string? evidenceId = null;
        string? evidenceHash = null;
        if (kind == WorkspaceActionPreconditionKind.ExpectedContentHash)
        {
            if (!value.TryGetProperty("expectedContentHash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            expectedHash = ReadString(hashValue);
        }
        else if (kind == WorkspaceActionPreconditionKind.ExpectedGovernedVersion)
        {
            if (!value.TryGetProperty("expectedGovernedVersion", out var versionValue)
                || !versionValue.TryGetInt64(out var parsedVersion)
                || !value.TryGetProperty("priorAfterEvidenceId", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("priorAfterEvidenceHash", out var evidenceHashValue)
                || evidenceHashValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            version = parsedVersion;
            evidenceId = ReadString(idValue);
            evidenceHash = ReadString(evidenceHashValue);
        }

        var candidate = new WorkspaceActionPrecondition(kind, expectedHash, version, evidenceId, evidenceHash);
        if (!ValidatePrecondition(candidate))
        {
            return false;
        }
        precondition = candidate;
        reasonCode = null;
        return true;
    }

    private static bool ValidatePrecondition(WorkspaceActionPrecondition precondition)
        => precondition.Kind switch
        {
            WorkspaceActionPreconditionKind.ExpectedAbsent => precondition.ExpectedContentHash is null
                && precondition.ExpectedGovernedVersion is null
                && precondition.PriorAfterEvidenceId is null
                && precondition.PriorAfterEvidenceHash is null,
            WorkspaceActionPreconditionKind.ExpectedContentHash => WorkspaceActionFingerprint.IsCanonicalSha256(precondition.ExpectedContentHash)
                && precondition.ExpectedGovernedVersion is null
                && precondition.PriorAfterEvidenceId is null
                && precondition.PriorAfterEvidenceHash is null,
            WorkspaceActionPreconditionKind.ExpectedGovernedVersion => precondition.ExpectedContentHash is null
                && precondition.ExpectedGovernedVersion is > 0
                && WorkspaceActionFingerprint.IsEvidenceIdentifier(precondition.PriorAfterEvidenceId)
                && WorkspaceActionFingerprint.IsCanonicalSha256(precondition.PriorAfterEvidenceHash),
            _ => false,
        };

    private static bool TryReadSegments(JsonElement value, out IReadOnlyList<WorkspaceActionContentSegment>? segments, out string? reasonCode)
    {
        segments = null;
        reasonCode = "workspace-content-segments-invalid";
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var values = value.EnumerateArray().Take(WorkspaceActionContractLimits.MaxContentSegments + 1).ToArray();
        if (values.Length > WorkspaceActionContractLimits.MaxContentSegments)
        {
            reasonCode = "workspace-content-segments-too-many";
            return false;
        }

        var captured = new List<WorkspaceActionContentSegment>(values.Length);
        var totalLiteralBytes = 0;
        var credentialReferences = 0;
        foreach (var segmentValue in values)
        {
            if (segmentValue.ValueKind != JsonValueKind.Object
                || !segmentValue.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var kind = ReadString(kindValue);
            if (kind == "literalUtf8")
            {
                if (!HasExactProperties(segmentValue, "kind", "literal")
                    || !segmentValue.TryGetProperty("literal", out var literalValue)
                    || literalValue.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                var literal = ReadString(literalValue)!;
                if (literal.Length > WorkspaceActionContractLimits.MaxLiteralCharacters)
                {
                    reasonCode = "workspace-literal-too-large";
                    return false;
                }
                totalLiteralBytes = checked(totalLiteralBytes + _strictUtf8.GetByteCount(literal));
                if (totalLiteralBytes > WorkspaceActionContractLimits.MaxLiteralUtf8Bytes)
                {
                    reasonCode = "workspace-literal-bytes-too-large";
                    return false;
                }
                captured.Add(new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.LiteralUtf8, literal, null));
                continue;
            }
            if (kind == "credentialReference")
            {
                if (!HasExactProperties(segmentValue, "credentialReferenceId", "kind")
                    || !segmentValue.TryGetProperty("credentialReferenceId", out var referenceValue)
                    || referenceValue.ValueKind != JsonValueKind.String
                    || !WorkspaceActionFingerprint.IsEvidenceIdentifier(ReadString(referenceValue))
                    || ++credentialReferences > WorkspaceActionContractLimits.MaxCredentialReferences)
                {
                    return false;
                }
                captured.Add(new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.CredentialReference, null, ReadString(referenceValue)));
                continue;
            }
            return false;
        }

        segments = Array.AsReadOnly(captured.ToArray());
        reasonCode = null;
        return true;
    }

    private static string EncodeUnchecked(WorkspaceActionInput input)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WritePropertyName("precondition");
        WritePrecondition(writer, input.Precondition);
        writer.WriteNumber("schemaVersion", input.SchemaVersion);
        writer.WriteString("scopeId", input.ScopeId.Value);
        writer.WritePropertyName("segments");
        writer.WriteStartArray();
        foreach (var segment in input.Segments)
        {
            writer.WriteStartObject();
            if (segment.Kind == WorkspaceActionContentSegmentKind.CredentialReference)
            {
                writer.WriteString("credentialReferenceId", segment.CredentialReferenceId);
                writer.WriteString("kind", "credentialReference");
            }
            else
            {
                writer.WriteString("kind", "literalUtf8");
                writer.WriteString("literal", segment.Literal);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("target", input.Target.Value);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WritePrecondition(Utf8JsonWriter writer, WorkspaceActionPrecondition precondition)
    {
        writer.WriteStartObject();
        switch (precondition.Kind)
        {
            case WorkspaceActionPreconditionKind.ExpectedAbsent:
                writer.WriteString("kind", "expectedAbsent");
                break;
            case WorkspaceActionPreconditionKind.ExpectedContentHash:
                writer.WriteString("expectedContentHash", precondition.ExpectedContentHash);
                writer.WriteString("kind", "expectedContentHash");
                break;
            case WorkspaceActionPreconditionKind.ExpectedGovernedVersion:
                writer.WriteNumber("expectedGovernedVersion", precondition.ExpectedGovernedVersion!.Value);
                writer.WriteString("kind", "expectedGovernedVersion");
                writer.WriteString("priorAfterEvidenceHash", precondition.PriorAfterEvidenceHash);
                writer.WriteString("priorAfterEvidenceId", precondition.PriorAfterEvidenceId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(precondition), "The workspace precondition is unsupported.");
        }
        writer.WriteEndObject();
    }

    private static bool HasExactProperties(JsonElement value, params string[] names)
    {
        var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == names.Length
            && properties.Distinct(StringComparer.Ordinal).Count() == names.Length
            && properties.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static string? ReadString(JsonElement value) => value.GetString();
}
