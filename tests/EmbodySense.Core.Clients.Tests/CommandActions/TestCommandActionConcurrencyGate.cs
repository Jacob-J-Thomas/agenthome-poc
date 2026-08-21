using EmbodySense.Core.Application.CommandActions;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class TestCommandActionConcurrencyGate : ICommandActionConcurrencyGate
{
    public bool IsAvailable => true;

    public Task<IAsyncDisposable?> TryAcquireAsync(string templateHash, int maximumConcurrency, TimeSpan waitLimit, CancellationToken cancellationToken = default)
        => Task.FromResult<IAsyncDisposable?>(TestCommandActionConcurrencyLease.Instance);
}
