using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class TestCapabilityExecutableArtifactLease : ICapabilityExecutableArtifactLease
{
    private FileStream? _executable;

    internal TestCapabilityExecutableArtifactLease(string artifactRoot, string executablePath, CapabilityIntegrityDigest artifactDigest, long activationRevision, string? reportedArtifactRoot = null)
    {
        ArtifactRoot = reportedArtifactRoot ?? artifactRoot;
        ExecutablePath = executablePath;
        ArtifactDigest = artifactDigest;
        ActivationRevision = activationRevision;
        _executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string ArtifactRoot { get; }

    public string ExecutablePath { get; }

    public SafeFileHandle ExecutableHandle => _executable?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(TestCapabilityExecutableArtifactLease));

    public CapabilityIntegrityDigest ArtifactDigest { get; }

    public long ActivationRevision { get; }

    public ValueTask DisposeAsync()
    {
        _executable?.Dispose();
        _executable = null;
        return ValueTask.CompletedTask;
    }
}
