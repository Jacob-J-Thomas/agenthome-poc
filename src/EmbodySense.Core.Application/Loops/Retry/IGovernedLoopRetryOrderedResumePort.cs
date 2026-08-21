using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Resolves and re-enters the canonical ordered runtime for one exact reserved retry attempt.</summary>
public interface IGovernedLoopRetryOrderedResumePort
{
    /// <summary>Resolves the immutable anchor, plan, and graph artifact pinned by the run.</summary>
    Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default);

    /// <summary>Re-enters the ordered runtime for an exact durable retry dispatch or routed exhaustion.</summary>
    Task<CustomLoopOrderedRunResult> ResumeRetryAsync(GovernedLoopRetryOrderedResumeRequest request, CancellationToken cancellationToken = default);
}
