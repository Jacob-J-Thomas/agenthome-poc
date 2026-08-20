namespace EmbodySense.Core.Persistence.Triggers.Schedules.Models;

/// <summary>Represents one exact bounded schema-version-1 workspace schedule catalog generation.</summary>
internal sealed record ScheduleStoreCatalog
{
    public ScheduleStoreCatalog(int schemaVersion, long generation, IEnumerable<ScheduleStoreEntry> entries)
    {
        SchemaVersion = schemaVersion;
        Generation = generation;
        Entries = Array.AsReadOnly((entries ?? throw new ArgumentNullException(nameof(entries)))
            .OrderBy(entry => entry.Definition.ScheduleId)
            .ToArray());
    }

    public int SchemaVersion { get; }

    public long Generation { get; }

    public IReadOnlyList<ScheduleStoreEntry> Entries { get; }
}
