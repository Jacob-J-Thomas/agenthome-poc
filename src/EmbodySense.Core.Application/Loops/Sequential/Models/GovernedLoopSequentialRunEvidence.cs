using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Provides the immutable canonical hand-off evidence persisted for one ordered runtime run.</summary>
/// <param name="AdapterBinding">The exact admitted run, revision, generation, graph, and receipt binding.</param>
/// <param name="InvocationSnapshot">The exact immutable non-secret invocation snapshot.</param>
public sealed record GovernedLoopSequentialRunEvidence(
    GovernedLoopSequentialAdapterBinding AdapterBinding,
    GovernedLoopSequentialInvocationSnapshot InvocationSnapshot);
