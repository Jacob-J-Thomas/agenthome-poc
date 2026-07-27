namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopAttemptCancellationResult(
    CustomLoopAttemptCancellationStatus Status,
    string Detail,
    string? OwnerId = null,
    int? OwnerProcessId = null);
