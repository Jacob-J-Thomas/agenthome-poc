using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Loops.Execution.Effects;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Parses, validates, and materializes caller-supplied typed values without composing a command string.</summary>
public static class CommandActionInputContract
{
    /// <summary>Returns whether a value has the exact canonical identifier shape admitted by command Action slots.</summary>
    /// <param name="value">The candidate identifier value.</param>
    /// <returns><see langword="true"/> only for a bounded lowercase slash-delimited capability path.</returns>
    public static bool IsCanonicalIdentifier(string? value)
        => CapabilityIdentifierRules.IsPath(value, 128);

    /// <summary>Parses exact canonical input for one server-selected template.</summary>
    public static bool TryParse(string? canonicalJson, CommandActionTemplate template, out CommandActionInput? input, out string? reasonCode)
    {
        input = null;
        reasonCode = "command-input-invalid";
        if (CommandActionTemplateContract.Validate(template) is not null
            || !GovernedActuatorInputContract.TryCanonicalize(canonicalJson, out var canonical, out _))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(canonical!.CanonicalJson, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, "schemaVersion", "templateHash", "templateId", "templateVersion", "values")
                || !root.TryGetProperty("schemaVersion", out var schemaValue)
                || !schemaValue.TryGetInt32(out var schemaVersion)
                || schemaVersion != CommandActionContractLimits.CurrentSchemaVersion
                || !root.TryGetProperty("templateId", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !string.Equals(idValue.GetString(), template.TemplateId, StringComparison.Ordinal)
                || !root.TryGetProperty("templateVersion", out var versionValue)
                || !versionValue.TryGetInt64(out var templateVersion)
                || templateVersion != template.TemplateVersion
                || !root.TryGetProperty("templateHash", out var hashValue)
                || hashValue.ValueKind != JsonValueKind.String
                || !string.Equals(hashValue.GetString(), template.ContentHash, StringComparison.Ordinal)
                || !root.TryGetProperty("values", out var valuesElement)
                || !TryReadValues(valuesElement, template, out var values, out reasonCode))
            {
                return false;
            }
            input = new CommandActionInput(schemaVersion, template.TemplateId, templateVersion, template.ContentHash, values!);
            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            reasonCode = "command-input-malformed";
            return false;
        }
    }

    /// <summary>Encodes validated semantic input as deterministic compact JSON.</summary>
    public static string Encode(CommandActionInput input, CommandActionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(template);
        var encoded = EncodeUnchecked(input);
        if (!TryParse(encoded, template, out var parsed, out var reasonCode))
        {
            throw new ArgumentException(reasonCode ?? "The command action input is invalid.", nameof(input));
        }
        return EncodeUnchecked(parsed!);
    }

    /// <summary>Materializes complete tokens, fixed environment, and optional standard input from validated input.</summary>
    public static bool TryMaterialize(string? canonicalJson, CommandActionTemplate template, out CommandActionMaterialization? materialization, out string? reasonCode)
    {
        materialization = null;
        if (!TryParse(canonicalJson, template, out var input, out reasonCode))
        {
            return false;
        }
        var values = input!.Values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        var arguments = new List<string>(template.Arguments.Count);
        var totalBytes = 0;
        foreach (var part in template.Arguments)
        {
            var token = part.Kind == CommandActionArgumentPartKind.Fixed ? part.Value : values[part.Value];
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(token));
            if (totalBytes > CommandActionContractLimits.MaxMaterializedInputUtf8Bytes)
            {
                reasonCode = "command-input-materialization-too-large";
                return false;
            }
            arguments.Add(token);
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in template.Environment)
        {
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(entry.Value));
            if (totalBytes > CommandActionContractLimits.MaxMaterializedInputUtf8Bytes)
            {
                reasonCode = "command-input-materialization-too-large";
                return false;
            }
            environment.Add(entry.Name, entry.Value);
        }
        var stdin = template.StandardInputSlot is null ? null : values[template.StandardInputSlot];
        totalBytes = checked(totalBytes + (stdin is null ? 0 : Encoding.UTF8.GetByteCount(stdin)));
        if (totalBytes > CommandActionContractLimits.MaxMaterializedInputUtf8Bytes)
        {
            reasonCode = "command-input-materialization-too-large";
            return false;
        }
        var canonical = Encode(input, template);
        materialization = new CommandActionMaterialization(
            arguments,
            environment,
            stdin,
            CommandActionFingerprint.Compute("embodysense.command-action-input.v1", canonical));
        reasonCode = null;
        return true;
    }

    private static bool TryReadValues(JsonElement element, CommandActionTemplate template, out IReadOnlyList<CommandActionSlotValue>? values, out string? reasonCode)
    {
        values = null;
        reasonCode = "command-input-values-invalid";
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var entries = element.EnumerateArray().Take(CommandActionContractLimits.MaxSlots + 1).ToArray();
        if (entries.Length != template.Slots.Count)
        {
            return false;
        }
        var captured = new List<CommandActionSlotValue>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var definition = template.Slots[index];
            if (entry.ValueKind != JsonValueKind.Object
                || !HasExactProperties(entry, "kind", "name", "value")
                || !entry.TryGetProperty("name", out var nameValue)
                || nameValue.ValueKind != JsonValueKind.String
                || !string.Equals(nameValue.GetString(), definition.Name, StringComparison.Ordinal)
                || !entry.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String
                || ParseKind(kindValue.GetString()) != definition.Kind
                || !entry.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.String
                || !TryNormalizeValue(definition, valueElement.GetString(), out var value))
            {
                return false;
            }
            captured.Add(new CommandActionSlotValue(definition.Name, definition.Kind, value!));
        }
        values = Array.AsReadOnly(captured.ToArray());
        reasonCode = null;
        return true;
    }

    private static bool TryNormalizeValue(CommandActionSlotDefinition definition, string? candidate, out string? value)
    {
        value = null;
        if (!CommandActionTemplateContract.IsSafeLiteralToken(candidate, definition.MaxUtf8Bytes, allowEmpty: definition.Kind == CommandActionSlotKind.BoundedText)
            || candidate!.StartsWith('@')
            || !definition.AllowLeadingOption && candidate.StartsWith('-'))
        {
            return false;
        }
        switch (definition.Kind)
        {
            case CommandActionSlotKind.Identifier:
                if (!IsCanonicalIdentifier(candidate))
                {
                    return false;
                }
                value = candidate;
                return true;
            case CommandActionSlotKind.Integer:
                if (!long.TryParse(candidate, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                    || !string.Equals(integer.ToString(CultureInfo.InvariantCulture), candidate, StringComparison.Ordinal)
                    || integer < definition.MinimumInteger
                    || integer > definition.MaximumInteger)
                {
                    return false;
                }
                value = candidate;
                return true;
            case CommandActionSlotKind.Enumeration:
                if (!definition.EnumerationValues.Contains(candidate, StringComparer.Ordinal))
                {
                    return false;
                }
                value = candidate;
                return true;
            case CommandActionSlotKind.BoundedText:
                value = candidate;
                return true;
            case CommandActionSlotKind.WorkspaceRelativeTarget:
                if (!WorkspaceRelativeFileTarget.TryParse(candidate, out var target, out _))
                {
                    return false;
                }
                value = target!.Value;
                return true;
            case CommandActionSlotKind.BoundedJson:
                if (!GovernedActuatorInputContract.TryCanonicalize(candidate, out var canonical, out _)
                    || canonical!.Utf8ByteCount > definition.MaxUtf8Bytes)
                {
                    return false;
                }
                value = canonical.CanonicalJson;
                return true;
            default:
                return false;
        }
    }

    private static string EncodeUnchecked(CommandActionInput input)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", input.SchemaVersion);
        writer.WriteString("templateHash", input.TemplateHash);
        writer.WriteString("templateId", input.TemplateId);
        writer.WriteNumber("templateVersion", input.TemplateVersion);
        writer.WritePropertyName("values");
        writer.WriteStartArray();
        foreach (var value in input.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", KindName(value.Kind));
            writer.WriteString("name", value.Name);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static CommandActionSlotKind ParseKind(string? value)
        => value switch
        {
            "identifier" => CommandActionSlotKind.Identifier,
            "integer" => CommandActionSlotKind.Integer,
            "enumeration" => CommandActionSlotKind.Enumeration,
            "boundedText" => CommandActionSlotKind.BoundedText,
            "workspaceRelativeTarget" => CommandActionSlotKind.WorkspaceRelativeTarget,
            "boundedJson" => CommandActionSlotKind.BoundedJson,
            _ => CommandActionSlotKind.Unknown,
        };

    private static string KindName(CommandActionSlotKind kind)
        => kind switch
        {
            CommandActionSlotKind.Identifier => "identifier",
            CommandActionSlotKind.Integer => "integer",
            CommandActionSlotKind.Enumeration => "enumeration",
            CommandActionSlotKind.BoundedText => "boundedText",
            CommandActionSlotKind.WorkspaceRelativeTarget => "workspaceRelativeTarget",
            CommandActionSlotKind.BoundedJson => "boundedJson",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == names.Length
            && properties.Distinct(StringComparer.Ordinal).Count() == names.Length
            && properties.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
