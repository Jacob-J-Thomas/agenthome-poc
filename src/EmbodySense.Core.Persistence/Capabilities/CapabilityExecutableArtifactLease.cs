using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityExecutableArtifactLease : ICapabilityExecutableArtifactLease
{
    private CapabilityCatalogPathSession? _session;
    private FileStream? _executable;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly Func<CancellationToken, Task<bool>> _launchValidator;

    internal CapabilityExecutableArtifactLease(CapabilityCatalogPathSession session, FileStream executable, string artifactRoot, string executablePath, CapabilityIntegrityDigest artifactDigest, long activationRevision, ICapabilityAuthorityTransaction authorityTransaction, Func<CancellationToken, Task<bool>> launchValidator)
    {
        _session = session;
        _executable = executable;
        ArtifactRoot = artifactRoot;
        ExecutablePath = executablePath;
        ArtifactDigest = artifactDigest;
        ActivationRevision = activationRevision;
        _authorityTransaction = authorityTransaction;
        _launchValidator = launchValidator;
    }

    public string ArtifactRoot { get; }

    public string ExecutablePath { get; }

    public SafeFileHandle ExecutableHandle => _executable?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(CapabilityExecutableArtifactLease));

    public CapabilityIntegrityDigest ArtifactDigest { get; }

    public long ActivationRevision { get; }

    public async Task<ICapabilityExecutableLaunchFence?> AcquireLaunchFenceAsync(CancellationToken cancellationToken = default)
    {
        var authorityLease = await _authorityTransaction.AcquireValidatedLeaseAsync(_launchValidator, cancellationToken);
        return authorityLease is null ? null : new CapabilityExecutableLaunchFence(authorityLease);
    }

    public Task<TResult?> ExecuteWithLaunchFenceAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _authorityTransaction.ExecuteWithValidatedAuthorityAsync(_launchValidator, operation, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _executable?.Dispose();
        _executable = null;
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}
