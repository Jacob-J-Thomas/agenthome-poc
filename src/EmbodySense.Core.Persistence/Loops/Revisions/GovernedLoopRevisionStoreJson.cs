using System.Text.Json;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Loops.Revisions;

internal static class GovernedLoopRevisionStoreJson
{
    private static readonly HashSet<string> _statuses = CanonicalTokens(GovernedLoopRevisionLifecycleStatus.Unknown);
    private static readonly HashSet<string> _operationKinds = CanonicalTokens(GovernedLoopRevisionOperationKind.Unknown);
    private static readonly HashSet<string> _operationOutcomes = CanonicalTokens(GovernedLoopRevisionOperationOutcome.Unknown);
    private static readonly HashSet<string> _failureCodes = CanonicalTokens(GovernedLoopRevisionOperationFailureCode.Unknown);

    public static bool IsStrictBoundedDocument(JsonElement root, int maximumArtifacts, int maximumHeads, int maximumOperations)
    {
        if (root.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(root)
            || !HasCanonicalEnumTokens(root))
        {
            return false;
        }

        return HasBoundedArray(root, "artifacts", maximumArtifacts)
            && HasBoundedArray(root, "heads", maximumHeads)
            && HasBoundedArray(root, "operations", maximumOperations);
    }

    private static bool HasBoundedArray(JsonElement root, string propertyName, int maximumCount)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() <= maximumCount;
    }

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
                    "kind" => _operationKinds,
                    "outcome" => _operationOutcomes,
                    "failureCode" => _failureCodes,
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
