using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Failures;

internal sealed class DelegateGovernedLoopFailureObservationSource : IGovernedLoopFailureObservationSource
{
    private readonly Func<GovernedLoopFailureClassificationContext, CancellationToken, Task<IReadOnlyList<GovernedLoopFailureObservation>?>> _read;

    public DelegateGovernedLoopFailureObservationSource(Func<GovernedLoopFailureClassificationContext, CancellationToken, Task<IReadOnlyList<GovernedLoopFailureObservation>?>> read)
    {
        _read = read;
    }

    public Task<IReadOnlyList<GovernedLoopFailureObservation>?> ReadAsync(GovernedLoopFailureClassificationContext context, CancellationToken cancellationToken = default)
        => _read(context, cancellationToken);
}
