using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

internal static class HumanInputRequestStoreJson
{
    private static readonly HashSet<string> _statuses = CanonicalTokens(HumanInputRequestLifecycleStatus.Unknown);
    private static readonly HashSet<string> _expectedStatuses = new(StringComparer.Ordinal) { "unknown", "pending" };
    private static readonly HashSet<string> _outcomes = CanonicalTokens(HumanInputRequestLifecycleOperationOutcome.Unknown);
    private static readonly HashSet<string> _failureCodes = CanonicalTokens(HumanInputRequestLifecycleOperationFailureCode.Unknown);
    private static readonly HashSet<string> _privacyClasses = CanonicalTokens(HumanInputPrivacyClass.Unknown);
    private static readonly HashSet<string> _kinds = KindTokens();

    public static bool IsStrictBoundedDocument(
        JsonElement root,
        int maximumRequestVersions,
        int maximumHeads,
        int maximumOperations)
    {
        if (root.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(root)
            || !HasCanonicalEnumTokens(root))
        {
            return false;
        }

        return HasBoundedArray(root, "requestVersions", maximumRequestVersions)
            && HasBoundedArray(root, "heads", maximumHeads)
            && HasBoundedArray(root, "operations", maximumOperations);
    }

    private static bool HasBoundedArray(JsonElement root, string propertyName, int maximumCount)
        => root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() <= maximumCount;

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasCanonicalEnumTokens(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var allowed = property.Name switch
                {
                    "status" => _statuses,
                    "expectedLifecycleStatus" => _expectedStatuses,
                    "outcome" => _outcomes,
                    "failureCode" => _failureCodes,
                    "privacyClass" => _privacyClasses,
                    "kind" => _kinds,
                    _ => null
                };
                if (allowed is not null
                    && (property.Value.ValueKind != JsonValueKind.String
                        || !allowed.Contains(property.Value.GetString()!)))
                {
                    return false;
                }

                if (!HasCanonicalEnumTokens(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasCanonicalEnumTokens(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static HashSet<string> KindTokens()
    {
        var tokens = CanonicalTokens(HumanInputRequestLifecycleOperationKind.Unknown);
        tokens.UnionWith(CanonicalTokens(HumanInputResponseKind.Unknown));
        tokens.UnionWith(CanonicalTokens(HumanInputStructuredFieldKind.Unknown));
        tokens.UnionWith(CanonicalTokens(HumanInputReferenceKind.Unknown));
        tokens.UnionWith(CanonicalTokens(HumanInputResponsePolicyKind.Unknown));
        tokens.UnionWith(CanonicalTokens(HumanInputContinuationPolicyKind.Unknown));
        return tokens;
    }

    private static HashSet<string> CanonicalTokens<TEnum>(TEnum unsupported)
        where TEnum : struct, Enum
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (!EqualityComparer<TEnum>.Default.Equals(value, unsupported))
            {
                tokens.Add(JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()));
            }
        }

        return tokens;
    }
}
