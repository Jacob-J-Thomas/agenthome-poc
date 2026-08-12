using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Preserves the durable invocation origin when selecting a custom-loop resume executor.</summary>
internal sealed class CustomLoopOriginAwareResumeExecutor : ICustomLoopResumeExecutor
{
    private readonly ICustomLoopRunStore _runStore;
    private readonly ICustomLoopResumeExecutor _defaultExecutor;

    /// <summary>Creates a resume router over one durable run store and the canonical-aware default executor.</summary>
    public CustomLoopOriginAwareResumeExecutor(
        ICustomLoopRunStore runStore,
        ICustomLoopResumeExecutor defaultExecutor)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _defaultExecutor = defaultExecutor ?? throw new ArgumentNullException(nameof(defaultExecutor));
    }

    /// <inheritdoc />
    public async Task<CustomLoopOrderedRunResult> ResumeAsync(
        CustomLoopResumeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await _runStore.GetAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (run is not null
            && TriggerDispatchOperationId.IsValid(run.AdmissionOperationId)
            && run.SequentialAdapterBinding is null
            && run.SequentialInvocationSnapshot is null)
        {
            throw new TriggerOriginCanonicalHandoffRequiredException();
        }

        return await _defaultExecutor.ResumeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
