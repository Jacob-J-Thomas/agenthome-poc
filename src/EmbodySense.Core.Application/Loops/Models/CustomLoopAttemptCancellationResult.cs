namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop attempt cancellation result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Detail">The detail.</param>
/// <param name="OwnerId">The owner ID.</param>
/// <param name="OwnerProcessId">The owner process ID.</param>
public sealed record CustomLoopAttemptCancellationResult(
    CustomLoopAttemptCancellationStatus Status,
    string Detail,
    string? OwnerId = null,
    int? OwnerProcessId = null);
