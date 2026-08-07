using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

internal static class TriggerDeliveryReplayBindingHash
{
    internal static bool TryCompute(TriggerDeliveryEnvelope? envelope, out string? hash)
    {
        hash = null;
        if (!TriggerDeliveryJson.TrySerialize(envelope, out var json, out _))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteProperty(writer, root, "actorContext");
            WriteProperty(writer, root, "adapter");
            WriteProperty(writer, root, "authority");
            WriteProperty(writer, root, "deduplicationId");
            WriteProperty(writer, root, "invokingConversation");
            WriteProperty(writer, root, "kind");
            WriteProperty(writer, root, "loop");
            WriteProperty(writer, root, "payload");
            WriteProperty(writer, root, "publicationRequested");
            WriteProperty(writer, root, "schemaVersion");
            WriteSemanticTemporal(writer, root.GetProperty("temporal"));
            writer.WriteEndObject();
        }

        hash = Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
        return true;
    }

    private static void WriteProperty(Utf8JsonWriter writer, JsonElement root, string propertyName)
    {
        writer.WritePropertyName(propertyName);
        root.GetProperty(propertyName).WriteTo(writer);
    }

    private static void WriteSemanticTemporal(Utf8JsonWriter writer, JsonElement temporal)
    {
        writer.WritePropertyName("temporal");
        writer.WriteStartObject();
        WriteProperty(writer, temporal, "createdAtUtc");
        WriteProperty(writer, temporal, "deadlineUtc");
        WriteProperty(writer, temporal, "expiresAtUtc");
        WriteProperty(writer, temporal, "notBeforeUtc");
        WriteProperty(writer, temporal, "observedAtUtc");
        writer.WriteEndObject();
    }
}
