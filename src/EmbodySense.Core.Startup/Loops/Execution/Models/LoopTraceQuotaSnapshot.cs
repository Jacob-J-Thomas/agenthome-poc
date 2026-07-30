namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports actual, reserved, accounted, and bounded workspace trace-retention capacity.
/// </summary>
/// <param name="LiveTraceCount">The live trace count.</param>
/// <param name="TombstoneCount">The tombstone count.</param>
/// <param name="LiveTraceUtf8Bytes">The live trace utf8 bytes.</param>
/// <param name="TombstoneUtf8Bytes">The tombstone utf8 bytes.</param>
/// <param name="ActualStoredUtf8Bytes">The actual stored utf8 bytes.</param>
/// <param name="ActiveReservationCount">The active reservation count.</param>
/// <param name="ReservedCapacityUtf8Bytes">The reserved capacity utf8 bytes.</param>
/// <param name="AccountedUtf8Bytes">The accounted utf8 bytes.</param>
/// <param name="AvailableAccountedUtf8Bytes">The available accounted utf8 bytes.</param>
/// <param name="MaximumLiveTraceCount">The maximum live trace count.</param>
/// <param name="MaximumTombstoneCount">The maximum tombstone count.</param>
/// <param name="MaximumWorkspaceUtf8Bytes">The maximum workspace utf8 bytes.</param>
/// <param name="MaximumPerTraceUtf8Bytes">The maximum per trace utf8 bytes.</param>
/// <param name="DeletionOperationCount">The deletion operation count.</param>
/// <param name="MaximumDeletionOperationCount">The maximum deletion operation count.</param>
/// <param name="IsOverLimit">The is over limit.</param>
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
    int DeletionOperationCount,
    int MaximumDeletionOperationCount,
    bool IsOverLimit);
