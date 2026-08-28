using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class ReentrantCurrentArtifactLease(
    ReentrantCurrentArtifactResolver owner,
    string artifactRoot,
    string entryPoint,
    CapabilityIntegrityDigest artifactDigest,
    long activationRevision) : ICapabilityExecutableArtifactLease
{
    private readonly FileStream _executable = new(Path.Combine(artifactRoot, entryPoint), FileMode.Open, FileAccess.Read, FileShare.Read);

    public string ArtifactRoot => artifactRoot;

    public string ExecutablePath => _executable.Name;

    public SafeFileHandle ExecutableHandle => _executable.SafeFileHandle;

    public CapabilityIntegrityDigest ArtifactDigest => artifactDigest;

    public long ActivationRevision => activationRevision;

    public Task<ICapabilityExecutableLaunchFence?> AcquireLaunchFenceAsync(CancellationToken cancellationToken = default)
    {
        owner.RecordAcquireLaunchFence();
        throw new InvalidOperationException("A retained fence cannot escape the nested catalog authority transaction.");
    }

    public async Task<TResult?> ExecuteWithLaunchFenceAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        where TResult : class
    {
        owner.RecordExecuteLaunchFence();
        cancellationToken.ThrowIfCancellationRequested();
        return owner.Current ? await operation(cancellationToken) : null;
    }

    public ValueTask DisposeAsync()
    {
        _executable.Dispose();
        return ValueTask.CompletedTask;
    }
}
