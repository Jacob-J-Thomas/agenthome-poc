using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Failures;

public sealed class GovernedLoopFailureClassificationServiceTests
{
    [Fact]
    public async Task Service_collects_each_source_once_and_classifies_the_combined_snapshot()
    {
        var calls = 0;
        var sources = new[]
        {
            Source((_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([GovernedLoopFailureClassifierTests.Observation(GovernedLoopFailureObservationKind.DependencyUnavailable, "dependency", 'a')]);
            }),
            Source((_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([GovernedLoopFailureClassifierTests.Observation(GovernedLoopFailureObservationKind.AuthorityDenied, "authority", 'b')]);
            }),
        };
        var service = new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), sources);

        var result = await service.ClassifyAsync(GovernedLoopFailureClassifierTests.Context(), DateTimeOffset.UnixEpoch);

        Assert.Equal(2, calls);
        Assert.Equal(GovernedLoopFailureClass.AuthorityPermissionDenied, result.Evidence?.FailureClass);
        Assert.Equal(2, result.Evidence?.CausalEvidence.Count);
    }

    [Fact]
    public async Task Service_converts_null_empty_overbound_or_throwing_sources_to_integrity_review()
    {
        var invalidSources = new[]
        {
            Source((_, _) => Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>(null)),
            Source((_, _) => Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([])),
            Source((_, _) => Task.FromException<IReadOnlyList<GovernedLoopFailureObservation>?>(new InvalidOperationException("private adapter detail"))),
            Source((_, _) => Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>(Enumerable.Range(0, GovernedLoopFailureClassifier.MaxObservations + 1)
                .Select(index => GovernedLoopFailureClassifierTests.Observation(GovernedLoopFailureObservationKind.ValidationRejected, $"validation-{index}", (char)('a' + index % 6)))
                .ToArray())),
        };

        foreach (var source in invalidSources)
        {
            var service = new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), [source]);
            var result = await service.ClassifyAsync(GovernedLoopFailureClassifierTests.Context(), DateTimeOffset.UnixEpoch);

            Assert.Equal(GovernedLoopFailureClassificationStatus.ReviewBlocked, result.Status);
            Assert.Equal(GovernedLoopFailureClass.EvidenceIntegrityFailure, result.Evidence?.FailureClass);
            Assert.DoesNotContain("private", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Service_preserves_caller_cancellation_and_does_not_read_later_sources()
    {
        using var cancellation = new CancellationTokenSource();
        var laterCalled = false;
        var first = Source((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([]);
        });
        var later = Source((_, _) =>
        {
            laterCalled = true;
            return Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([GovernedLoopFailureClassifierTests.Observation(GovernedLoopFailureObservationKind.ValidationRejected, "validation", 'a')]);
        });
        var service = new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), [first, later]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ClassifyAsync(GovernedLoopFailureClassifierTests.Context(), DateTimeOffset.UnixEpoch, cancellation.Token));
        Assert.False(laterCalled);
    }

    [Fact]
    public void Service_rejects_empty_null_or_overbound_source_composition()
    {
        Assert.Throws<ArgumentException>(() => new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), []));
        Assert.Throws<ArgumentException>(() => new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), [null!]));
        Assert.Throws<ArgumentException>(() => new GovernedLoopFailureClassificationService(new GovernedLoopFailureClassifier(), Enumerable.Repeat(Source((_, _) => Task.FromResult<IReadOnlyList<GovernedLoopFailureObservation>?>([])), 33)));
    }

    private static DelegateGovernedLoopFailureObservationSource Source(Func<GovernedLoopFailureClassificationContext, CancellationToken, Task<IReadOnlyList<GovernedLoopFailureObservation>?>> read)
        => new(read);
}
