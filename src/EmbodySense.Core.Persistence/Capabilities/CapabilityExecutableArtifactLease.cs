using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityExecutableArtifactLease : ICapabilityExecutableArtifactLease
{
    private CapabilityCatalogPathSession? _session;
    private FileStream? _executable;

    internal CapabilityExecutableArtifactLease(CapabilityCatalogPathSession session, FileStream executable, string artifactRoot, string executablePath, CapabilityIntegrityDigest artifactDigest, long activationRevision)
    {
        _session = session;
        _executable = executable;
        ArtifactRoot = artifactRoot;
        ExecutablePath = executablePath;
        ArtifactDigest = artifactDigest;
        ActivationRevision = activationRevision;
    }

    public string ArtifactRoot { get; }

    public string ExecutablePath { get; }

    public SafeFileHandle ExecutableHandle => _executable?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(CapabilityExecutableArtifactLease));

    public CapabilityIntegrityDigest ArtifactDigest { get; }

    public long ActivationRevision { get; }

    public ValueTask DisposeAsync()
    {
        _executable?.Dispose();
        _executable = null;
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}
