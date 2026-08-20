using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal static class ScheduleRunAdmissionEvidenceCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static byte[] Serialize(ScheduleRunAdmissionEvidence evidence)
    {
        if (!ScheduleRunAdmissionEvidenceValidator.IsValid(evidence))
        {
            throw new FormatException("Schedule run-admission evidence is malformed or its content hash is invalid.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(evidence, _options);
        if (content.Length + 1 > ScheduleRunAdmissionEvidenceLimits.MaxArtifactUtf8Bytes)
        {
            throw new FormatException($"Schedule run-admission evidence exceeds {ScheduleRunAdmissionEvidenceLimits.MaxArtifactUtf8Bytes} UTF-8 bytes.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    internal static ScheduleRunAdmissionEvidence Deserialize(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is < 2 or > ScheduleRunAdmissionEvidenceLimits.MaxArtifactUtf8Bytes || content[^1] != (byte)'\n')
        {
            throw new FormatException("Schedule run-admission evidence is empty, unterminated, or exceeds its explicit bound.");
        }

        ScheduleRunAdmissionEvidence evidence;
        try
        {
            using var document = JsonDocument.Parse(content.AsMemory(0, content.Length - 1), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _options.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            evidence = JsonSerializer.Deserialize<ScheduleRunAdmissionEvidence>(content.AsSpan(0, content.Length - 1), _options)
                ?? throw new FormatException("Schedule run-admission evidence was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("Schedule run-admission evidence contains invalid JSON, unknown fields, missing fields, or unsupported enum values.", exception);
        }

        if (!ScheduleRunAdmissionEvidenceValidator.IsValid(evidence))
        {
            throw new FormatException("Schedule run-admission evidence is malformed or its content hash is invalid.");
        }

        var canonical = Serialize(evidence);
        if (!content.AsSpan().SequenceEqual(canonical))
        {
            throw new FormatException("Schedule run-admission evidence is not in its exact canonical JSON form.");
        }

        return evidence;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            names.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException($"Schedule run-admission evidence contains duplicate property `{path}.{property.Name}`.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", new HashSet<string>(StringComparer.Ordinal));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]", new HashSet<string>(StringComparer.Ordinal));
            }
        }
    }
}
