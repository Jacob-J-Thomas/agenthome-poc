using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Detaches mutable adapter-owned collections before posture validation or projection observes them.</summary>
internal static class GovernedLoopOperationalEvidenceDetacher
{
    internal static GovernedLoopScheduleEvidenceReadResult? Schedules(GovernedLoopScheduleEvidenceReadResult? source)
        => source is null
            ? null
            : new GovernedLoopScheduleEvidenceReadResult(
                source.Status,
                source.Generation,
                source.HasMore,
                source.ContinuationCursor,
                source.Items is null
                    ? null!
                    : Array.AsReadOnly(source.Items.Select(item => item is null
                        ? null!
                        : new GovernedLoopScheduleEvidenceSnapshot(
                            ScheduleContractCopy.Copy(item.Definition)!,
                            ScheduleContractCopy.Copy(item.State)!)).ToArray()));

    internal static GovernedLoopWakeCatalogEvidenceReadResult? Wakes(GovernedLoopWakeCatalogEvidenceReadResult? source)
        => source is null
            ? null
            : new GovernedLoopWakeCatalogEvidenceReadResult(
                source.Status,
                source.Generation,
                source.HasMore,
                source.ContinuationCursor,
                source.Items is null
                    ? null!
                    : Array.AsReadOnly(source.Items.Select(item => item is null
                        ? null!
                        : new GovernedLoopWakeEvidenceSnapshot(
                            item.Checkpoint is null ? null! : item.Checkpoint with { },
                            item.Wake is null ? null : item.Wake with { })).ToArray()));
}
