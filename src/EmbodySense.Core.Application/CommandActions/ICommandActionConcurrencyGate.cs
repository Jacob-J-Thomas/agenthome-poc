namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Owns a bounded cross-process admission slot for one exact command template.</summary>
public interface ICommandActionConcurrencyGate
{
    /// <summary>Gets whether durable cross-process admission is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Attempts to acquire one of the template's finite slots within a bounded wait.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string templateHash, int maximumConcurrency, TimeSpan waitLimit, CancellationToken cancellationToken = default);
}
