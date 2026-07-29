using EmbodySense.Core.Clients.LocalWorkspace.Models;

namespace EmbodySense.Core.Clients.LocalWorkspace;

internal sealed class ListEntryComparer : IComparer<ListEntry>
{
    public static ListEntryComparer Instance { get; } = new();

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
