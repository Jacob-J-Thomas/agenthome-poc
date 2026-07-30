using EmbodySense.Core.Clients.LocalWorkspace.Models;

namespace EmbodySense.Core.Clients.LocalWorkspace;

/// <summary>
/// Orders directory entries before files, then by case-insensitive name, exact name, and canonical path.
/// </summary>
internal sealed class ListEntryComparer : IComparer<ListEntry>
{
    /// <summary>
    /// Gets the shared stateless comparer.
    /// </summary>
    /// <value>The shared comparer instance.</value>
    public static ListEntryComparer Instance { get; } = new();

    /// <summary>
    /// Compares two entries using the deterministic workspace-list ordering.
    /// </summary>
    /// <param name="left">The left.</param>
    /// <param name="right">The right.</param>
    /// <returns>A negative value when <paramref name="left"/> sorts first, zero when equal, or a positive value otherwise.</returns>
    public int Compare(ListEntry? left, ListEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var kind = right.IsDirectory.CompareTo(left.IsDirectory);
        if (kind != 0)
        {
            return kind;
        }

        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (name != 0)
        {
            return name;
        }

        name = StringComparer.Ordinal.Compare(left.Name, right.Name);
        return name != 0 ? name : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }
}
