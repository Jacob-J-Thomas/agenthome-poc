using System.Text.Json;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal static class GovernedLoopAdmissionStoreJson
{
    public static bool IsStrictBoundedDocument(JsonElement root, int maximumOutcomes)
    {
        return root.ValueKind == JsonValueKind.Object
            && !HasDuplicateProperties(root)
            && root.TryGetProperty("outcomes", out var outcomes)
            && outcomes.ValueKind == JsonValueKind.Array
            && outcomes.GetArrayLength() <= maximumOutcomes;
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
