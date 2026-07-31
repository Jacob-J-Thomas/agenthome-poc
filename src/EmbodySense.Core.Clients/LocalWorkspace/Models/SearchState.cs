namespace EmbodySense.Core.Clients.LocalWorkspace.Models;

/// <summary>
/// Accumulates bounded workspace-search accounting and truncation metadata.
/// </summary>
internal sealed class SearchState
{
    /// <summary>
    /// Gets or sets the number of eligible files inspected.
    /// </summary>
    /// <value>The files scanned.</value>
    public int FilesScanned { get; set; }

    /// <summary>
    /// Gets or sets the number of EmbodySense-internal paths skipped.
    /// </summary>
    /// <value>The skipped internal files.</value>
    public int SkippedInternalFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files skipped because they exceeded the per-file byte limit.
    /// </summary>
    /// <value>The skipped large files.</value>
    public int SkippedLargeFiles { get; set; }

    /// <summary>
    /// Gets or sets whether any file, match, or traversal bound truncated the search.
    /// </summary>
    /// <value><see langword="true"/> when the returned search is incomplete because a bound was reached.</value>
    public bool Truncated { get; set; }
}
