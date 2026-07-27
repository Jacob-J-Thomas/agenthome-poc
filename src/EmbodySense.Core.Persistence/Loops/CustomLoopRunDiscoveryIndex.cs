namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopRunDiscoveryIndex(int SchemaVersion, long Revision, CustomLoopRunDiscoveryIndexEntry[] Entries)
{
    // TODO(#71): Reset experimental persisted schema versions to 1 and remove pre-1.0 compatibility handling.
    public const int CurrentSchemaVersion = 2;
}
