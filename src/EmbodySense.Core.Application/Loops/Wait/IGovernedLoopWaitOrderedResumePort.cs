using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Resolves and re-enters the one canonical ordered runtime from immutable Wait evidence.</summary>
public interface IGovernedLoopWaitOrderedResumePort
{
    /// <summary>Resolves the exact immutable anchor, plan, and artifact pinned by a canonical run.</summary>
    Task<GovernedLoopWaitOrderedContext?> ResolveAsync(
        CustomLoopRunRecord run,
        CancellationToken cancellationToken = default);

    /// <summary>Re-enters the canonical ordered runtime for one exact resumed Wait activation.</summary>
    Task<CustomLoopOrderedRunResult> ResumeAsync(
        GovernedLoopWaitOrderedResumeRequest request,
        CancellationToken cancellationToken = default);
}
