using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public static class CustomLoopAdmissionRequestHash
{
    public static string Compute(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteString(writer, "loopId", run.LoopId);
            WriteString(writer, "surface", run.Surface);
            writer.WritePropertyName("modelSnapshot");
            WriteModel(writer, run.ModelSnapshot);
            writer.WritePropertyName("admittedDefinition");
            WriteDefinitionIdentity(writer, run.AdmittedDefinition);
            WriteString(writer, "triggerPrompt", run.TriggerPrompt);
            writer.WritePropertyName("invokingConversation");
            WriteConversation(writer, run.InvokingConversation);
            writer.WritePropertyName("contextSnapshot");
            WriteContextSnapshot(writer, run.ContextSnapshot);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static CustomLoopRunRecord Apply(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run with { AdmissionRequestHash = Compute(run) };
    }

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
