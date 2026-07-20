namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopTraceQuotaSnapshot(
    int LiveTraceCount,
    int TombstoneCount,
    long LiveTraceUtf8Bytes,
    long TombstoneUtf8Bytes,
    long ActualStoredUtf8Bytes,
    int ActiveReservationCount,
    long ReservedCapacityUtf8Bytes,
    long AccountedUtf8Bytes,
    long AvailableAccountedUtf8Bytes,
    int MaximumLiveTraceCount,
    int MaximumTombstoneCount,
    long MaximumWorkspaceUtf8Bytes,
    int MaximumPerTraceUtf8Bytes,
    bool IsOverLimit);
