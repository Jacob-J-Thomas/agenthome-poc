using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Computes, applies, and verifies canonical sequential hand-off contract hashes.</summary>
public static class GovernedLoopSequentialContractHash
{
    /// <summary>Computes the canonical invocation-snapshot hash.</summary>
    public static string Compute(GovernedLoopSequentialInvocationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireValid(GovernedLoopSequentialContractValidator.ValidateForHash(snapshot), nameof(snapshot));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "governed-loop-sequential-invocation-v1");
            writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
            writer.WriteString("triggerPrompt", Normalize(snapshot.TriggerPrompt));
            writer.WritePropertyName("modelSnapshot");
            WriteModel(writer, snapshot.ModelSnapshot);
            writer.WritePropertyName("invokingConversation");
            WriteConversation(writer, snapshot.InvokingConversation);
            writer.WriteString("contextCapturedAtUtc", Timestamp(snapshot.ContextCapturedAtUtc));
            writer.WritePropertyName("contextManifest");
            writer.WriteStartArray();
            foreach (var source in snapshot.ContextManifest)
            {
                WriteContextSource(writer, source);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Digest(buffer.WrittenSpan);
    }

    /// <summary>Returns an invocation-snapshot copy carrying its canonical hash.</summary>
    public static GovernedLoopSequentialInvocationSnapshot Apply(GovernedLoopSequentialInvocationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { ContentHash = Compute(snapshot) };
    }

    /// <summary>Gets whether an invocation snapshot retains its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopSequentialInvocationSnapshot? snapshot)
        => snapshot is not null && Matches(snapshot.ContentHash, () => Compute(snapshot));

    /// <summary>Computes the canonical adapter-binding hash.</summary>
    public static string Compute(GovernedLoopSequentialAdapterBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RequireValid(GovernedLoopSequentialContractValidator.ValidateForHash(binding), nameof(binding));
        var canonical = new StringBuilder(1024);
        Append(canonical, "governed-loop-sequential-adapter-binding-v1");
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.WorkspaceId);
        Append(canonical, binding.ExecutionBinding.SchemaVersion);
        Append(canonical, binding.ExecutionBinding.RunId);
        Append(canonical, binding.ExecutionBinding.Revision.SchemaVersion);
        Append(canonical, binding.ExecutionBinding.Revision.GraphId);
        Append(canonical, binding.ExecutionBinding.Revision.RevisionId);
        Append(canonical, binding.ExecutionBinding.Revision.ExecutableHash);
        Append(canonical, binding.ExecutionBinding.ExecutionGeneration);
        Append(canonical, binding.AdmissionOperationId);
        Append(canonical, binding.AdmissionReceipt.ContentHash);
        Append(canonical, binding.AdmissionReceiptHash);
        Append(canonical, binding.AdmissionRequestHash);
        Append(canonical, binding.InvocationPayloadHash);
        Append(canonical, binding.GraphArtifactHash);
        Append(canonical, binding.GraphLayoutHash);
        return Digest(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    /// <summary>Returns an adapter-binding copy carrying its canonical hash.</summary>
    public static GovernedLoopSequentialAdapterBinding Apply(GovernedLoopSequentialAdapterBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding with { ContentHash = Compute(binding) };
    }

    /// <summary>Gets whether an adapter binding retains its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopSequentialAdapterBinding? binding)
        => binding is not null && Matches(binding.ContentHash, () => Compute(binding));

    private static void WriteModel(Utf8JsonWriter writer, CustomLoopModelSnapshot model)
    {
        writer.WriteStartObject();
        writer.WriteString("provider", Normalize(model.Provider));
        WriteNullableString(writer, "model", model.Model);
        writer.WriteEndObject();
    }

    private static void WriteConversation(Utf8JsonWriter writer, CustomLoopConversationReference? conversation)
    {
        if (conversation is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("conversationId", Normalize(conversation.ConversationId));
        writer.WriteString("capturedVersion", Normalize(conversation.CapturedVersion));
        writer.WriteString("capturedAtUtc", Timestamp(conversation.CapturedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteContextSource(Utf8JsonWriter writer, CustomLoopContextManifestSource source)
    {
        writer.WriteStartObject();
        writer.WriteNumber("order", source.Order);
        writer.WriteNumber("sourceType", (int)source.SourceType);
        writer.WriteString("sourceId", Normalize(source.SourceId));
        writer.WriteString("sourcePath", Normalize(source.SourcePath));
        writer.WriteNumber("provenance", (int)source.Provenance);
        writer.WriteNumber("trustClass", (int)source.TrustClass);
        writer.WriteNumber("role", (int)source.Role);
        writer.WriteString("content", Normalize(source.Content));
        writer.WriteString("contentHash", source.ContentHash);
        writer.WriteNumber("originalCharacterCount", source.OriginalCharacterCount);
        writer.WriteNumber("usedCharacterCount", source.UsedCharacterCount);
        writer.WriteBoolean("truncated", source.Truncated);
        WriteNullableString(writer, "truncationReason", source.TruncationReason);
        WriteNullableString(writer, "omissionReason", source.OmissionReason);
        writer.WriteString("capturedAtUtc", Timestamp(source.CapturedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, Normalize(value));
        }
    }

    private static bool Matches(string? actual, Func<string> compute)
    {
        if (actual is not { Length: GovernedLoopSequentialContractLimits.Sha256HexCharacters })
        {
            return false;
        }

        try
        {
            var expectedBytes = Encoding.ASCII.GetBytes(compute());
            var actualBytes = Encoding.ASCII.GetBytes(actual);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RequireValid(GovernedLoopSequentialValidationResult result, string parameterName)
    {
        if (!result.IsValid)
        {
            throw new ArgumentException($"Sequential governed-loop contract is invalid at {result.Errors[0].Path}.", parameterName);
        }
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC);

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder canonical, int value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        canonical.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(normalized);
    }

    private static string Digest(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
