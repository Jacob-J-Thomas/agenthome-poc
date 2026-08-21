using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Reuses the canonical immutable-context resolver and ordered runtime for exact retry re-entry.</summary>
public sealed class GovernedLoopSequentialRetryResumeExecutor : IGovernedLoopRetryOrderedResumePort
{
    private readonly IGovernedLoopWaitOrderedResumePort _contextResolver;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;

    /// <summary>Creates one retry re-entry boundary without adding another graph or execution runtime.</summary>
    public GovernedLoopSequentialRetryResumeExecutor(
        IGovernedLoopWaitOrderedResumePort contextResolver,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime)
    {
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
    }

    /// <inheritdoc />
    public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(
        CustomLoopRunRecord run,
        CancellationToken cancellationToken = default)
        => _contextResolver.ResolveAsync(run, cancellationToken);

    /// <inheritdoc />
    public Task<CustomLoopOrderedRunResult> ResumeRetryAsync(
        GovernedLoopRetryOrderedResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        return _orderedRuntime.ResumeRetryAsync(
            new GovernedLoopSequentialOrderedRetryResumeRequest(
                GovernedLoopSequentialOrderedRetryResumeRequest.CurrentSchemaVersion,
                request.Context.Anchor,
                request.Context.Plan,
                request.Context.Artifact,
                request.RetryState,
                request.Actor),
            cancellationToken);
    }
}
