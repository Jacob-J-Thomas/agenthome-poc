using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

internal static class CapabilityCatalogPageSnapshot
{
    internal const int MaximumEntryCount = 100;

    internal static IReadOnlyList<CapabilityCatalogEntry> Capture(IReadOnlyList<CapabilityCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var declaredCount = entries.Count;
        if (declaredCount is < 0 or > MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(entries), "A capability catalog page must remain within its finite entry bound.");
        }

        var captured = entries.Take(MaximumEntryCount + 1).ToArray();
        if (captured.Length != declaredCount || captured.Any(entry => entry is null))
        {
            throw new ArgumentException("A capability catalog page must expose one coherent, non-null entry snapshot.", nameof(entries));
        }

        return Array.AsReadOnly(captured);
    }
}
