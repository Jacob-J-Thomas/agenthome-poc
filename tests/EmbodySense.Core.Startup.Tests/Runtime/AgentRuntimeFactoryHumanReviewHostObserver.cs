using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class AgentRuntimeFactoryHumanReviewHostObserver : IGovernedLoopLocalCoordinatorBoundaryObserver, IDisposable
{
    private readonly CustomLoopRunStore _runs;
    private readonly string _runId;
    private readonly TaskCompletionSource<bool> _humanReviewWorkAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _firstHumanReviewWorkAttempted;

    internal AgentRuntimeFactoryHumanReviewHostObserver(string workspaceRoot, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runs = new CustomLoopRunStore(new WorkspacePaths(workspaceRoot));
        _runId = runId;
    }

    internal Task HumanReviewWorkAttempted => _humanReviewWorkAttempted.Task;

    internal bool FirstHumanReviewWorkSawPublishedContinuation { get; private set; }

    public void OnHeartbeatDue()
    {
    }

    public void OnWorkFamilyAttempted(GovernedLoopLocalWorkFamily family)
    {
        if (family != GovernedLoopLocalWorkFamily.HumanReview || Interlocked.Exchange(ref _firstHumanReviewWorkAttempted, 1) != 0)
        {
            return;
        }

        try
        {
            var run = _runs.GetAsync(_runId).GetAwaiter().GetResult();
            FirstHumanReviewWorkSawPublishedContinuation = run?.HumanReview?.Continuation is not null;
        }
        catch
        {
            FirstHumanReviewWorkSawPublishedContinuation = false;
        }

        _humanReviewWorkAttempted.TrySetResult(true);
    }

    public void Dispose() => _runs.Dispose();
}
