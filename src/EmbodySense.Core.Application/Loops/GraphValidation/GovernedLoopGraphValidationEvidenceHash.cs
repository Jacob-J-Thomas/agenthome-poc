using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

internal static class GovernedLoopGraphValidationEvidenceHash
{
    public static GovernedLoopGraphValidationEvidence Compute(GovernedLoopNodeCatalogSnapshot catalog, GovernedLoopAuthoritySnapshot authority)
    {
        var catalogHash = Hash(writer => WriteCatalog(writer, catalog));
        var authorityHash = Hash(writer => WriteAuthority(writer, authority));
        var combinedHash = Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("authorityHash", authorityHash);
            writer.WriteString("catalogHash", catalogHash);
            writer.WriteEndObject();
        });
        return new GovernedLoopGraphValidationEvidence(catalogHash, authorityHash, combinedHash);
    }

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteCatalog(Utf8JsonWriter writer, GovernedLoopNodeCatalogSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("sourceEvidenceId", snapshot.SourceEvidenceId);
        writer.WritePropertyName("descriptors");
        writer.WriteStartArray();
        foreach (var item in snapshot.Descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("descriptor", DescriptorKey(item));
            writer.WriteBoolean("advertised", item.IsAdvertised);
            writer.WriteBoolean("executable", item.IsExecutable);
            writer.WriteBoolean("entry", item.IsLegalEntry);
            writer.WriteBoolean("terminal", item.IsLegalTerminal);
            WriteEnums(writer, "allowedOutcomes", item.AllowedControlOutcomes);
            WriteEnums(writer, "requiredOutcomes", item.RequiredControlOutcomes);
            writer.WriteString("joinPolicy", item.JoinPolicy.ToString());
            writer.WriteNumber("minimumIncoming", item.MinimumIncomingControlEdges);
            writer.WriteBoolean("allowsCycle", item.AllowsCycle);
            writer.WriteString("cycleIterations", item.CycleIterationBudgetParameterId);
            writer.WriteString("cycleMilliseconds", item.CycleTimeBudgetMillisecondsParameterId);
            writer.WritePropertyName("ports");
            writer.WriteStartArray();
            foreach (var port in item.Ports.OrderBy(port => port.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", port.Id);
                writer.WriteString("direction", port.Direction.ToString());
                writer.WriteString("bindingKind", port.BindingKind.ToString());
                writer.WriteString("valueKind", port.ValueKind.ToString());
                writer.WriteBoolean("required", port.Required);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var parameter in item.Parameters.OrderBy(parameter => parameter.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", parameter.Id);
                writer.WriteString("valueKind", parameter.ValueKind.ToString());
                writer.WriteBoolean("required", parameter.Required);
                writer.WriteNumber("minimumCharacters", parameter.MinimumCharacters);
                writer.WriteNumber("maximumCharacters", parameter.MaximumCharacters);
                if (parameter.MinimumInteger.HasValue)
                {
                    writer.WriteNumber("minimumInteger", parameter.MinimumInteger.Value);
                }
                else
                {
                    writer.WriteNull("minimumInteger");
                }

                if (parameter.MaximumInteger.HasValue)
                {
                    writer.WriteNumber("maximumInteger", parameter.MaximumInteger.Value);
                }
                else
                {
                    writer.WriteNull("maximumInteger");
                }

                writer.WritePropertyName("allowedValues");
                writer.WriteStartArray();
                foreach (var value in parameter.AllowedValues.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in item.RequiredCapabilityIds.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("resources");
            writer.WriteNumber("attempts", item.ResourceBudget.Attempts);
            writer.WriteNumber("payloadCharacters", item.ResourceBudget.PayloadCharacters);
            writer.WriteNumber("evidenceItems", item.ResourceBudget.EvidenceItems);
            writer.WriteNumber("resourceUnits", item.ResourceBudget.ResourceUnits);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAuthority(Utf8JsonWriter writer, GovernedLoopAuthoritySnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("sourceEvidenceId", snapshot.SourceEvidenceId);
        writer.WriteString("roleId", snapshot.RoleId);
        writer.WritePropertyName("capabilities");
        writer.WriteStartArray();
        foreach (var capability in snapshot.CapabilityIds.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(capability);
        }

        writer.WriteEndArray();
        writer.WriteNumber("maxAttempts", snapshot.MaxAttempts);
        writer.WriteNumber("maxPayloadCharacters", snapshot.MaxPayloadCharacters);
        writer.WriteNumber("maxEvidenceItems", snapshot.MaxEvidenceItems);
        writer.WriteNumber("maxResourceUnits", snapshot.MaxResourceUnits);
        writer.WriteEndObject();
    }

    private static void WriteEnums<TEnum>(Utf8JsonWriter writer, string name, IEnumerable<TEnum> values) where TEnum : struct, Enum
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(value => Convert.ToInt32(value)))
        {
            writer.WriteStringValue(value.ToString());
        }

        writer.WriteEndArray();
    }

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
    {
        return $"{Convert.ToInt32(descriptor.Descriptor.Kind):D4}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
    }
}
