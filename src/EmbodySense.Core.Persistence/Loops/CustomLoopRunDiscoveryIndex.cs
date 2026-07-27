namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopRunDiscoveryIndex(int SchemaVersion, long Revision, CustomLoopRunDiscoveryIndexEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;
}
