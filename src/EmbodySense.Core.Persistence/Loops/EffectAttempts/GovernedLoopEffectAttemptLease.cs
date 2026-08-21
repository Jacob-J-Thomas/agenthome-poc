using EmbodySense.Core.Application.Loops.EffectAttempts;

namespace EmbodySense.Core.Persistence.Loops.EffectAttempts;

internal sealed class GovernedLoopEffectAttemptLease(
    Guid storeInstanceId,
    string operationId,
    long effectGeneration,
    FileStream stream) : IGovernedLoopEffectAttemptLease
{
    private FileStream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    internal bool Owns(Guid candidateStoreInstanceId, string candidateOperationId, long candidateEffectGeneration)
        => storeInstanceId == candidateStoreInstanceId
            && string.Equals(operationId, candidateOperationId, StringComparison.Ordinal)
            && effectGeneration == candidateEffectGeneration
            && Volatile.Read(ref _stream) is not null;

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
