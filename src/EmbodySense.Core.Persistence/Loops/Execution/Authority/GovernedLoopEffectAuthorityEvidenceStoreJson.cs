using System.Text.Json;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority;

internal static class GovernedLoopEffectAuthorityEvidenceStoreJson
{
    public static bool IsStrictBoundedDocument(JsonElement root, int maximumDecisions, int maximumTargetReservations, int maximumCompletionClaims)
    {
        return root.ValueKind == JsonValueKind.Object
            && !HasDuplicateProperties(root)
            && root.TryGetProperty("decisions", out var decisions)
            && decisions.ValueKind == JsonValueKind.Array
            && decisions.GetArrayLength() <= maximumDecisions
            && root.TryGetProperty("targetReservations", out var targetReservations)
            && targetReservations.ValueKind == JsonValueKind.Array
            && targetReservations.GetArrayLength() <= maximumTargetReservations
            && root.TryGetProperty("completionClaims", out var completionClaims)
            && completionClaims.ValueKind == JsonValueKind.Array
            && completionClaims.GetArrayLength() <= maximumCompletionClaims;
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
}
