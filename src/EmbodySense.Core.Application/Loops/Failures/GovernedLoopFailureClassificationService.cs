using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Failures;

/// <summary>Collects bounded adapter observations and delegates policy-free classification to the canonical classifier.</summary>
public sealed class GovernedLoopFailureClassificationService
{
    private readonly IGovernedLoopFailureClassifier _classifier;
    private readonly IReadOnlyList<IGovernedLoopFailureObservationSource> _sources;

    /// <summary>Creates one bounded classifier composition.</summary>
    public GovernedLoopFailureClassificationService(IGovernedLoopFailureClassifier classifier, IEnumerable<IGovernedLoopFailureObservationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(sources);
        var snapshot = sources.Take(33).ToArray();
        if (snapshot.Length is < 1 or > 32 || snapshot.Any(source => source is null))
        {
            throw new ArgumentException("Failure classification requires one to thirty-two non-null observation sources.", nameof(sources));
        }
        _classifier = classifier;
        _sources = Array.AsReadOnly(snapshot);
    }

    /// <summary>Collects observations once and returns a classified or review-blocked result.</summary>
    /// <param name="context">The exact immutable run-node-attempt coordinates.</param>
    /// <param name="observedAtUtc">The trusted UTC classification time.</param>
    /// <param name="cancellationToken">Cancels before classification is committed.</param>
    public async Task<GovernedLoopFailureClassificationResult> ClassifyAsync(GovernedLoopFailureClassificationContext context, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var observations = new List<GovernedLoopFailureObservation>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reported = await source.ReadAsync(context, cancellationToken).ConfigureAwait(false);
                if (reported is null || reported.Count < 1 || observations.Count + reported.Count > GovernedLoopFailureClassifier.MaxObservations)
                {
                    return _classifier.Classify(context, [IntegrityObservation(context, "classification-source-result-invalid")], observedAtUtc);
                }
                observations.AddRange(reported);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return _classifier.Classify(context, [IntegrityObservation(context, "classification-source-threw")], observedAtUtc);
            }
        }
        return _classifier.Classify(context, Array.AsReadOnly(observations.ToArray()), observedAtUtc);
    }

    private static GovernedLoopFailureObservation IntegrityObservation(GovernedLoopFailureClassificationContext context, string serverCode)
        => new(GovernedLoopFailureObservationKind.EvidenceIntegrityFailure, GovernedLoopFailureSource.Evidence, serverCode, context.ClassificationBoundaryEvidence);
}
