using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Evaluates derived facts over otherwise data-only canonical sequential results.</summary>
public static class GovernedLoopSequentialResultFacts
{
    /// <summary>Gets whether the ordered runtime reported that a provider call occurred.</summary>
    public static bool ProviderWasInvoked(this GovernedLoopSequentialInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Execution?.ProviderWasInvoked == true;
    }

    /// <summary>Gets whether the exact run has a durable admission-audit boundary and may be considered for lifecycle-aware execution.</summary>
    public static bool IsReady(this GovernedLoopSequentialMaterializationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (result.Status is GovernedLoopSequentialMaterializationStatus.Ready or GovernedLoopSequentialMaterializationStatus.Replayed)
            && result.Run is not null
            && CustomLoopRunValidator.HasCompleteAdmissionAudit(result.Run);
    }
}
