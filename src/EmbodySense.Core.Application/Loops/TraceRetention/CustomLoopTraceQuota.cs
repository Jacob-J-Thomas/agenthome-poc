using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceQuota(
    int RetainedTraceCount,
    long ActualTraceUtf8Bytes,
    long AccountedTraceUtf8Bytes,
    int ActiveReservationCount,
    int MaximumTraceCount,
    long MaximumWorkspaceUtf8Bytes,
    int MaximumPerTraceUtf8Bytes,
    int TombstoneCount = 0,
    long TombstoneUtf8Bytes = 0,
    int MaximumTombstoneCount = CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
{
    public long ReservedCapacityUtf8Bytes => Math.Max(0, AccountedTraceUtf8Bytes - ActualTraceUtf8Bytes - TombstoneUtf8Bytes);

    public long ActualStoredUtf8Bytes => checked(ActualTraceUtf8Bytes + TombstoneUtf8Bytes);

    public long AvailableAccountedUtf8Bytes => Math.Max(0, MaximumWorkspaceUtf8Bytes - AccountedTraceUtf8Bytes);

    public bool IsOverLimit => RetainedTraceCount > MaximumTraceCount || TombstoneCount > MaximumTombstoneCount || AccountedTraceUtf8Bytes > MaximumWorkspaceUtf8Bytes;

    public static CustomLoopTraceQuota Empty() => new(
        0,
        0,
        0,
        0,
        CustomLoopLimits.MaxRunTracesPerWorkspace,
        CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes,
        CustomLoopLimits.MaxRunTraceUtf8Bytes,
        0,
        0,
        CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace);
}
