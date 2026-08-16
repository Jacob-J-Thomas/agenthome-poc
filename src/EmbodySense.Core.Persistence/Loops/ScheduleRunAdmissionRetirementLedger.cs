using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed record ScheduleRunAdmissionRetirementLedger(
    int SchemaVersion,
    IReadOnlyList<ScheduleRunAdmissionRetirement> Entries,
    string ContentHash)
{
    private IReadOnlyList<ScheduleRunAdmissionRetirement>? _entries = Entries is null
        ? null
        : Array.AsReadOnly(Entries.Take(ScheduleRunAdmissionRetirementCodec.MaximumSchedules + 1).ToArray());

    public IReadOnlyList<ScheduleRunAdmissionRetirement> Entries
    {
        get => _entries!;
        init => _entries = value is null
            ? null
            : Array.AsReadOnly(value.Take(ScheduleRunAdmissionRetirementCodec.MaximumSchedules + 1).ToArray());
    }
}
