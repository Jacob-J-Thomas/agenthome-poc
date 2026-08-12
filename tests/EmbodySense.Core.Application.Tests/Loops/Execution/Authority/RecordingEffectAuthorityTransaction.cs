using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class RecordingEffectAuthorityTransaction : ICapabilityAuthorityTransaction
{
    private readonly AsyncLocal<int> _depth = new();

    internal int Executions { get; private set; }

    internal bool IsInside => _depth.Value > 0;

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Executions++;
        _depth.Value++;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _depth.Value--;
        }
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
