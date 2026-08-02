using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class TestCapabilityExecutableArtifactLease : ICapabilityExecutableArtifactLease
{
    private FileStream? _executable;
    private readonly bool _launchAllowed;
    private readonly bool _waitForLaunchCancellation;

    internal TestCapabilityExecutableArtifactLease(string artifactRoot, string executablePath, CapabilityIntegrityDigest artifactDigest, long activationRevision, string? reportedArtifactRoot = null, bool launchAllowed = true, bool waitForLaunchCancellation = false)
    {
        ArtifactRoot = reportedArtifactRoot ?? artifactRoot;
        ExecutablePath = executablePath;
        ArtifactDigest = artifactDigest;
        ActivationRevision = activationRevision;
        _launchAllowed = launchAllowed;
        _waitForLaunchCancellation = waitForLaunchCancellation;
        _executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal TaskCompletionSource LaunchFenceAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ArtifactRoot { get; }

    public string ExecutablePath { get; }

    public SafeFileHandle ExecutableHandle => _executable?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(TestCapabilityExecutableArtifactLease));

    public CapabilityIntegrityDigest ArtifactDigest { get; }

    public long ActivationRevision { get; }

    public async Task<ICapabilityExecutableLaunchFence?> AcquireLaunchFenceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_waitForLaunchCancellation)
        {
            LaunchFenceAttempted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return _launchAllowed ? new TestCapabilityExecutableLaunchFence() : null;
    }

    public ValueTask DisposeAsync()
    {
        _executable?.Dispose();
        _executable = null;
        return ValueTask.CompletedTask;
    }
}
