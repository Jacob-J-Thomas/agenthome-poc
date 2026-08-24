using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class DelegatingGovernedLoopFailureClassifier(
    Func<GovernedLoopFailureClassificationContext, IReadOnlyList<GovernedLoopFailureObservation>, DateTimeOffset, GovernedLoopFailureClassificationResult> classify)
    : IGovernedLoopFailureClassifier
{
    public GovernedLoopFailureClassificationResult Classify(
        GovernedLoopFailureClassificationContext context,
        IReadOnlyList<GovernedLoopFailureObservation> observations,
        DateTimeOffset observedAtUtc)
        => classify(context, observations, observedAtUtc);
}
