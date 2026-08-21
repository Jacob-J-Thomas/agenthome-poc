using EmbodySense.Core.Application.Credentials.Leases;

namespace EmbodySense.Core.Persistence.Credentials.Leases;

internal sealed class CredentialLeaseAttemptLease(
    Guid storeInstanceId,
    string operationId,
    long generation,
    FileStream stream) : ICredentialLeaseAttemptLease
{
    private FileStream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    internal bool Owns(Guid candidateInstanceId, string candidateOperationId, long candidateGeneration)
        => storeInstanceId == candidateInstanceId
            && string.Equals(operationId, candidateOperationId, StringComparison.Ordinal)
            && generation == candidateGeneration
            && Volatile.Read(ref _stream) is not null;

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
