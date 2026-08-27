using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

/// <summary>Encodes the canonical, closed schema-1 Human Input policy-generation membership proof.</summary>
internal static class HumanInputPolicyFileStoreGenerationJson
{
    private const int SchemaVersion = 1;

    public static byte[] Serialize(HumanInputPolicyFileStoreGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.StoreGeneration < 0 || generation.StoreGeneration != generation.Artifacts.Count || generation.Artifacts.Count > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        var artifacts = generation.Artifacts.OrderBy(entry => entry.Reference.ToString(), StringComparer.Ordinal).ToArray();
        if (artifacts.Length != generation.Artifacts.Count || !generation.Artifacts.SequenceEqual(artifacts) || artifacts.Any(entry => !IsEntry(entry)) || artifacts.Select(entry => entry.Reference).Distinct().Count() != artifacts.Length)
        {
            throw new ArgumentException("The Human Input policy generation membership proof is invalid or noncanonical.", nameof(generation));
        }

        var builder = new StringBuilder();
        builder.Append("{\"schemaVersion\":1,\"storeGeneration\":");
        builder.Append(generation.StoreGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"artifacts\":[");
        for (var index = 0; index < artifacts.Length; index++)
        {
            if (index > 0) builder.Append(',');
            var entry = artifacts[index];
            builder.Append("{\"policyId\":");
            builder.Append(JsonSerializer.Serialize(entry.Reference.PolicyId));
            builder.Append(",\"revisionId\":");
            builder.Append(JsonSerializer.Serialize(entry.Reference.RevisionId));
            builder.Append(",\"contentHash\":");
            builder.Append(JsonSerializer.Serialize(entry.ContentHash));
            builder.Append('}');
        }
        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static HumanInputPolicyFileStoreGeneration Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1) throw new FormatException("The Human Input policy generation is empty.");

        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The Human Input policy generation is invalid.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 3
            || !string.Equals(properties[0].Name, "schemaVersion", StringComparison.Ordinal)
            || !string.Equals(properties[1].Name, "storeGeneration", StringComparison.Ordinal)
            || !string.Equals(properties[2].Name, "artifacts", StringComparison.Ordinal)
            || properties[0].Value.ValueKind != JsonValueKind.Number
            || properties[1].Value.ValueKind != JsonValueKind.Number
            || properties[2].Value.ValueKind != JsonValueKind.Array
            || !properties[0].Value.TryGetInt32(out var schemaVersion)
            || schemaVersion != SchemaVersion
            || !properties[1].Value.TryGetInt64(out var storeGeneration)
            || storeGeneration < 0)
        {
            throw new FormatException("The Human Input policy generation is invalid.");
        }

        var entries = new List<HumanInputPolicyFileStoreCatalogEntry>();
        foreach (var element in properties[2].Value.EnumerateArray())
        {
            if (entries.Count >= 1_024 || element.ValueKind != JsonValueKind.Object) throw new FormatException("The Human Input policy generation is invalid.");
            var entryProperties = element.EnumerateObject().ToArray();
            if (entryProperties.Length != 3
                || !string.Equals(entryProperties[0].Name, "policyId", StringComparison.Ordinal)
                || !string.Equals(entryProperties[1].Name, "revisionId", StringComparison.Ordinal)
                || !string.Equals(entryProperties[2].Name, "contentHash", StringComparison.Ordinal)
                || entryProperties.Any(property => property.Value.ValueKind != JsonValueKind.String))
            {
                throw new FormatException("The Human Input policy generation is invalid.");
            }

            var policyId = entryProperties[0].Value.GetString();
            var revisionId = entryProperties[1].Value.GetString();
            var contentHash = entryProperties[2].Value.GetString();
            if (string.IsNullOrEmpty(policyId)
                || string.IsNullOrEmpty(revisionId)
                || string.IsNullOrEmpty(contentHash)
                || !HumanInputPolicyReference.TryParse(policyId + "@" + revisionId, out var reference))
            {
                throw new FormatException("The Human Input policy generation is invalid.");
            }
            entries.Add(new HumanInputPolicyFileStoreCatalogEntry(reference!, contentHash));
        }

        var generation = new HumanInputPolicyFileStoreGeneration(storeGeneration, entries);
        if (!Serialize(generation).AsSpan().SequenceEqual(bytes)) throw new FormatException("The Human Input policy generation is not canonical.");
        return generation;
    }

    private static bool IsEntry(HumanInputPolicyFileStoreCatalogEntry entry)
        => entry is not null
            && HumanInputPolicyReference.TryParse(entry.Reference.ToString(), out _)
            && entry.ContentHash.Length == 64
            && entry.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
