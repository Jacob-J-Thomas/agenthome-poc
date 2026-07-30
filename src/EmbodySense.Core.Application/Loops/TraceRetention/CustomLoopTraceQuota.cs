using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace quota.
/// </summary>
/// <param name="RetainedTraceCount">The retained trace count.</param>
/// <param name="ActualTraceUtf8Bytes">The actual trace UTF-8 bytes.</param>
/// <param name="AccountedTraceUtf8Bytes">The accounted trace UTF-8 bytes.</param>
/// <param name="ActiveReservationCount">The active reservation count.</param>
/// <param name="MaximumTraceCount">The maximum trace count.</param>
/// <param name="MaximumWorkspaceUtf8Bytes">The maximum workspace UTF-8 bytes.</param>
/// <param name="MaximumPerTraceUtf8Bytes">The maximum per trace UTF-8 bytes.</param>
/// <param name="TombstoneCount">The tombstone count.</param>
/// <param name="TombstoneUtf8Bytes">The tombstone UTF-8 bytes.</param>
/// <param name="MaximumTombstoneCount">The maximum tombstone count.</param>
/// <param name="DeletionOperationCount">The deletion operation count.</param>
/// <param name="MaximumDeletionOperationCount">The maximum deletion operation count.</param>
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
    int MaximumTombstoneCount = CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace,
    int DeletionOperationCount = 0,
    int MaximumDeletionOperationCount = CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace)
{
    /// <summary>
    /// Gets the reserved capacity UTF-8 bytes.
    /// </summary>
    /// <value>The reserved capacity UTF-8 bytes.</value>
    public long ReservedCapacityUtf8Bytes => Math.Max(0, AccountedTraceUtf8Bytes - ActualTraceUtf8Bytes - TombstoneUtf8Bytes);

    /// <summary>
    /// Gets the actual stored UTF-8 bytes.
    /// </summary>
    /// <value>The actual stored UTF-8 bytes.</value>
    public long ActualStoredUtf8Bytes => checked(ActualTraceUtf8Bytes + TombstoneUtf8Bytes);

    /// <summary>
    /// Gets the available accounted UTF-8 bytes.
    /// </summary>
    /// <value>The available accounted UTF-8 bytes.</value>
    public long AvailableAccountedUtf8Bytes => Math.Max(0, MaximumWorkspaceUtf8Bytes - AccountedTraceUtf8Bytes);

    /// <summary>
    /// Gets a value indicating whether the value is over limit.
    /// </summary>
    /// <value><see langword="true"/> when the value is over limit; otherwise, <see langword="false"/>.</value>
    public bool IsOverLimit => RetainedTraceCount > MaximumTraceCount || TombstoneCount > MaximumTombstoneCount || DeletionOperationCount > MaximumDeletionOperationCount || AccountedTraceUtf8Bytes > MaximumWorkspaceUtf8Bytes;

    /// <summary>
    /// Creates a custom loop trace quota representing empty.
    /// </summary>
    /// <returns>The custom loop trace quota.</returns>
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
        CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace,
        0,
        CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace);
}
