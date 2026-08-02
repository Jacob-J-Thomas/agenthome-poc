using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class CredentialLifecycleRegistryProbe
{
    private readonly CredentialRegistryStore _store;
    private readonly WorkspacePaths _paths;

    internal CredentialLifecycleRegistryProbe(WorkspacePaths paths, CredentialRegistryStore store)
    {
        _paths = paths;
        _store = store;
    }

    internal long Revision => ReadAsync().GetAwaiter().GetResult().RegistryRevision ?? 0;
    internal IReadOnlyList<CredentialRegistryOperationEvidence> Mutations => ReadAsync().GetAwaiter().GetResult().Operations;

    internal Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    internal Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => _store.MutateAsync(mutation, cancellationToken);

    internal void MakeUnavailable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.CredentialRegistryDocumentPath)!);
        File.WriteAllText(_paths.CredentialRegistryDocumentPath, "{}");
    }
}
