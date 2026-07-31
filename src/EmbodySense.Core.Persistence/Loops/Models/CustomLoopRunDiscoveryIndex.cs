namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a custom loop run discovery index.
/// </summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="Revision">The revision.</param>
/// <param name="Entries">The entries.</param>
internal sealed record CustomLoopRunDiscoveryIndex(int SchemaVersion, long Revision, CustomLoopRunDiscoveryIndexEntry[] Entries)
{
    /// <summary>
    /// Identifies the current schema version custom loop run discovery index.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
