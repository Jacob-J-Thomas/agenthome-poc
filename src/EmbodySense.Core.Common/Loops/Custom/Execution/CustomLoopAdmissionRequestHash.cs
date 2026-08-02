using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Computes, applies, and verifies the canonical custom loop admission request hash.
/// </summary>
public static class CustomLoopAdmissionRequestHash
{
    /// <summary>
    /// Computes the lowercase SHA-256 digest of the immutable, behavior-affecting admission request.
    /// </summary>
    /// <param name="run">The run whose loop, surface, actor, model, admitted definition identity, trigger, conversation, and context snapshot identity are serialized canonically.</param>
    /// <returns>A 64-character lowercase hexadecimal digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="run"/> is <see langword="null"/>.</exception>
    public static string Compute(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteString(writer, "loopId", run.LoopId);
            WriteString(writer, "surface", run.Surface);
            WriteString(writer, "admissionActor", run.AdmissionActor);
            writer.WritePropertyName("modelSnapshot");
            WriteModel(writer, run.ModelSnapshot);
            writer.WritePropertyName("admittedDefinition");
            WriteDefinitionIdentity(writer, run.AdmittedDefinition);
            WriteString(writer, "triggerPrompt", run.TriggerPrompt);
            writer.WritePropertyName("invokingConversation");
            WriteConversation(writer, run.InvokingConversation);
            writer.WritePropertyName("contextSnapshot");
            WriteContextSnapshot(writer, run.ContextSnapshot);
            writer.WritePropertyName("capabilityAdmission");
            JsonSerializer.Serialize(writer, run.CapabilityAdmission);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a copy of a run with its canonical admission-request hash applied.
    /// </summary>
    /// <param name="run">The admitted run to hash.</param>
    /// <returns>A copy whose admission-request hash matches <see cref="Compute"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="run"/> is <see langword="null"/>.</exception>
    public static CustomLoopRunRecord Apply(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run with { AdmissionRequestHash = Compute(run) };
    }

    /// <summary>
    /// Determines whether a run retains the exact canonical admission-request hash.
    /// </summary>
    /// <param name="run">The run to verify.</param>
    /// <returns><see langword="true"/> when stored and recomputed ASCII digests have equal length and fixed-time equality; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="run"/> is <see langword="null"/>.</exception>
    public static bool Matches(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var expected = Encoding.ASCII.GetBytes(Compute(run));
        var actual = Encoding.ASCII.GetBytes(run.AdmissionRequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteModel(Utf8JsonWriter writer, CustomLoopModelSnapshot? model)
    {
        if (model is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "provider", model.Provider);
        WriteString(writer, "model", model.Model);
        writer.WriteEndObject();
    }

    private static void WriteDefinitionIdentity(Utf8JsonWriter writer, CustomLoopDefinition? definition)
    {
        if (definition is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "id", definition.Id);
        writer.WriteNumber("definitionVersion", definition.DefinitionVersion);
        WriteString(writer, "contentHash", definition.ContentHash);
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
        WriteString(writer, "conversationId", conversation.ConversationId);
        WriteString(writer, "capturedVersion", conversation.CapturedVersion);
        WriteTimestamp(writer, "capturedAtUtc", conversation.CapturedAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteContextSnapshot(Utf8JsonWriter writer, CustomLoopContextSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
        WriteTimestamp(writer, "capturedAtUtc", snapshot.CapturedAtUtc);
        WriteString(writer, "manifestHash", snapshot.ManifestHash);
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
    {
        writer.WriteString(propertyName, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
