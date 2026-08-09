using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

/// <summary>Observes an in-memory lifecycle registry while keeping raw mutation attempts fail-closed.</summary>
internal sealed class CredentialLifecycleRegistryProbe
{
    private readonly InMemoryCredentialLifecycleRegistryStore _store;

    internal CredentialLifecycleRegistryProbe(string authenticatedActorId, DateTimeOffset timestamp)
    {
        _store = new InMemoryCredentialLifecycleRegistryStore(authenticatedActorId, timestamp);
    }

    internal ICredentialRegistryStore LifecycleStore => _store;
    internal long Revision => ReadAsync().GetAwaiter().GetResult().RegistryRevision ?? 0;
    internal IReadOnlyList<CredentialRegistryOperationEvidence> Mutations => ReadAsync().GetAwaiter().GetResult().Operations;

    internal Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => _store.ReadDiagnosticAsync(cancellationToken);

    internal Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CredentialRegistryMutationResult(CredentialRegistryMutationStatus.Invalid, mutation.OperationId, Revision, null, CredentialFailure.FromCode(CredentialFailureCode.Unauthorized)));

    internal void MakeUnavailable() => _store.MakeUnavailable();
}
