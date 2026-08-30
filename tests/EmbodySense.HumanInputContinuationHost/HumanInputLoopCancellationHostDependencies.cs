using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.HumanInputContinuationHost;

/// <summary>Supplies non-dispatching lifecycle dependencies and an optional crash point after the durable CancelRequested run transition.</summary>
internal sealed class HumanInputLoopCancellationHostDependencies(bool exitAfterRunCancellationCommit) :
    IAuditLog,
    ICustomLoopResumeExecutor,
    ICustomLoopModelAvailability,
    ICustomLoopExecutionCancellationSignal
{
    private readonly bool _exitAfterRunCancellationCommit = exitAfterRunCancellationCommit;
    private int _exited;

    public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_exitAfterRunCancellationCommit && Interlocked.Exchange(ref _exited, 1) == 0)
        {
            Environment.Exit(86);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    public Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "The cancellation-process host does not resume custom-loop execution."));

    public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public IDisposable? TryRegisterActiveRun(string runId) => null;

    public void CancelActiveAttempt(string runId)
    {
    }

    public Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(
        string runId,
        string operationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.NoActiveAttempt, "The cancellation-process host owns no active provider attempt."));
}
