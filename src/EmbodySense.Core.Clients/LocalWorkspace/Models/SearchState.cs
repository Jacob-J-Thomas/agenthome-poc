namespace EmbodySense.Core.Clients.LocalWorkspace.Models;

internal sealed class SearchState
{
    public int FilesScanned { get; set; }

    public int SkippedInternalFiles { get; set; }

    public int SkippedLargeFiles { get; set; }

    public bool Truncated { get; set; }
}
