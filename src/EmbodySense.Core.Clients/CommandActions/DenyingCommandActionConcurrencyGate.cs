using EmbodySense.Core.Application.CommandActions;

namespace EmbodySense.Core.Clients.CommandActions;

/// <summary>Fails closed when no durable cross-process command-admission gate is composed.</summary>
public sealed class DenyingCommandActionConcurrencyGate : ICommandActionConcurrencyGate
{
    /// <summary>Gets the shared denying instance.</summary>
    public static DenyingCommandActionConcurrencyGate Instance { get; } = new();

    private DenyingCommandActionConcurrencyGate()
    {
    }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<IAsyncDisposable?> TryAcquireAsync(string templateHash, int maximumConcurrency, TimeSpan waitLimit, CancellationToken cancellationToken = default)
        => Task.FromResult<IAsyncDisposable?>(null);
}
