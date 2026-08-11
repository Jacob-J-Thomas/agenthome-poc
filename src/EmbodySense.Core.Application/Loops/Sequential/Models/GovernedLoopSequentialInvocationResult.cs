using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns canonical admission, materialization, and optional ordered-runtime evidence without surface-specific projection.</summary>
/// <param name="Status">The closed coordination status.</param>
/// <param name="Admission">The canonical admission result when admission was attempted.</param>
/// <param name="Materialization">The durable run materialization result when admission succeeded.</param>
/// <param name="Execution">The ordered-runtime result only when first dispatch was permitted.</param>
/// <param name="Run">The latest authenticated run known to the coordinator.</param>
/// <param name="Detail">A bounded non-secret diagnostic.</param>
public sealed record GovernedLoopSequentialInvocationResult(
    GovernedLoopSequentialInvocationStatus Status,
    GovernedLoopAdmissionResult? Admission,
    GovernedLoopSequentialMaterializationResult? Materialization,
    CustomLoopOrderedRunResult? Execution,
    CustomLoopRunRecord? Run,
    string Detail)
{
    /// <summary>Gets whether the ordered runtime reported that a provider call occurred.</summary>
    public bool ProviderWasInvoked => Execution?.ProviderWasInvoked == true;
}
