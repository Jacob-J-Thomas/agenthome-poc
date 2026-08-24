using EmbodySense.Core.Application.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Failures;

/// <summary>Classifies bounded observations using the closed schema-1 precedence contract.</summary>
public interface IGovernedLoopFailureClassifier
{
    /// <summary>Classifies one exact failure observation set.</summary>
    GovernedLoopFailureClassificationResult Classify(GovernedLoopFailureClassificationContext context, IReadOnlyList<GovernedLoopFailureObservation> observations, DateTimeOffset observedAtUtc);
}
