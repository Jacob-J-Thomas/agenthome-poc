using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal static class ScheduleRunAdmissionRetirementCodec
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumSchedules = 4_096;
    internal const int MaximumArtifactUtf8Bytes = 4 * 1024 * 1024;
    internal const int RetainedTerminalEvidencePerSchedule = 2;

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };

    internal static ScheduleRunAdmissionRetirementLedger Empty()
        => Apply(new ScheduleRunAdmissionRetirementLedger(CurrentSchemaVersion, [], string.Empty));

    internal static ScheduleRunAdmissionRetirementLedger Apply(ScheduleRunAdmissionRetirementLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var normalized = ledger with
        {
            Entries = ledger.Entries?.OrderBy(item => item.ScheduleId, StringComparer.Ordinal).ToArray()!,
            ContentHash = string.Empty,
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, _options)));
        return normalized with { ContentHash = hash };
    }

    internal static byte[] Serialize(ScheduleRunAdmissionRetirementLedger ledger)
    {
        if (!IsValid(ledger))
        {
            throw new FormatException("Schedule run-admission retirement evidence is malformed or its content hash is invalid.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(ledger, _options);
        if (content.Length + 1 > MaximumArtifactUtf8Bytes)
        {
            throw new FormatException($"Schedule run-admission retirement evidence exceeds {MaximumArtifactUtf8Bytes} UTF-8 bytes.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    internal static ScheduleRunAdmissionRetirementLedger Deserialize(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is < 2 or > MaximumArtifactUtf8Bytes || content[^1] != (byte)'\n')
        {
            throw new FormatException("Schedule run-admission retirement evidence is empty, unterminated, or exceeds its explicit bound.");
        }

        ScheduleRunAdmissionRetirementLedger ledger;
        try
        {
            using var document = JsonDocument.Parse(content.AsMemory(0, content.Length - 1), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _options.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            ledger = JsonSerializer.Deserialize<ScheduleRunAdmissionRetirementLedger>(content.AsSpan(0, content.Length - 1), _options)
                ?? throw new FormatException("Schedule run-admission retirement evidence was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("Schedule run-admission retirement evidence contains invalid JSON, unknown fields, or missing fields.", exception);
        }

        if (!IsValid(ledger) || !content.AsSpan().SequenceEqual(Serialize(ledger)))
        {
            throw new FormatException("Schedule run-admission retirement evidence is malformed, noncanonical, or its content hash is invalid.");
        }

        return ledger;
    }

    internal static bool Covers(ScheduleRunAdmissionRetirement retirement, ScheduleExecutionDirective directive)
        => string.Equals(retirement.ScheduleId, directive.ScheduleId.Value, StringComparison.Ordinal)
            && (retirement.ScheduleRevision > directive.DefinitionRevision
                || retirement.ScheduleRevision == directive.DefinitionRevision
                && string.Equals(retirement.DefinitionHash, directive.DefinitionHash, StringComparison.Ordinal)
                && retirement.RetiredThroughOccurrenceOrdinal >= directive.Occurrence.Ordinal);

    internal static int Compare(ScheduleRunAdmissionRetirement left, ScheduleRunAdmissionRetirement right)
    {
        var revision = left.ScheduleRevision.CompareTo(right.ScheduleRevision);
        if (revision != 0)
        {
            return revision;
        }

        return left.RetiredThroughOccurrenceOrdinal.CompareTo(right.RetiredThroughOccurrenceOrdinal);
    }

    private static bool IsValid(ScheduleRunAdmissionRetirementLedger? ledger)
    {
        if (ledger is null
            || ledger.SchemaVersion != CurrentSchemaVersion
            || ledger.Entries is null
            || ledger.Entries.Count > MaximumSchedules
            || ledger.ContentHash?.Length != ScheduleContractLimits.Sha256HexCharacters
            || !ledger.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        string? previous = null;
        foreach (var entry in ledger.Entries)
        {
            if (entry is null
                || entry.SchemaVersion != CurrentSchemaVersion
                || !ScheduleId.TryParse(entry.ScheduleId, out _)
                || entry.ScheduleRevision is < 1 or > ScheduleContractLimits.MaxRevision
                || entry.DefinitionHash?.Length != ScheduleContractLimits.Sha256HexCharacters
                || !entry.DefinitionHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                || entry.RetiredThroughOccurrenceOrdinal is < 1 or > ScheduleContractLimits.MaxOccurrenceOrdinal
                || entry.RetiredThroughScheduledAtUtc.Offset != TimeSpan.Zero
                || entry.RetiredAtUtc.Offset != TimeSpan.Zero
                || entry.RetiredAtUtc < entry.RetiredThroughScheduledAtUtc
                || previous is not null && string.Compare(previous, entry.ScheduleId, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            previous = entry.ScheduleId;
        }

        return string.Equals(Apply(ledger).ContentHash, ledger.ContentHash, StringComparison.Ordinal);
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
                    throw new FormatException($"Schedule run-admission retirement evidence contains duplicate property `{path}.{property.Name}`.");
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
