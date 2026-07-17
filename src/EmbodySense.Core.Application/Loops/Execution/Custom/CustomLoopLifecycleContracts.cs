using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopPauseRequest(string RunId, int ExpectedLifecycleVersion, string OperationId, string Actor);

public sealed record CustomLoopCancelRequest(string RunId, int ExpectedLifecycleVersion, string OperationId, string Actor);

public sealed record CustomLoopResumeRequest(string RunId, int ExpectedLifecycleVersion, string OperationId, string Actor);

public sealed record CustomLoopControlResult(CustomLoopControlStatus Status, CustomLoopRunRecord? Run, string OperationId, string Detail);

public interface ICustomLoopExecutionCancellationSignal
{
    void CancelActiveAttempt(string runId);
}

public sealed record CustomLoopResumeExecutionRequest(string RunId, int RunningLifecycleVersion, string ResumeOperationId, string Actor);

public interface ICustomLoopResumeExecutor
{
    Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface ICustomLoopModelAvailability
{
    Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default);
}

public enum CustomLoopRecoveryStatus
{
    Unknown = 0,
    Unchanged = 1,
    Paused = 2,
    Cancelled = 3,
    NeedsReview = 4,
    Conflict = 5,
    Failed = 6
}

public sealed record CustomLoopRecoveryResult(CustomLoopRecoveryStatus Status, CustomLoopRunRecord Run, string Detail);
