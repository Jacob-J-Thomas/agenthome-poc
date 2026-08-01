using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Clients.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class TestCapabilityProcessIsolationBoundary : ICapabilityProcessIsolationBoundary
{
    internal CapabilityExecutableAvailability Availability { get; set; } = new(CapabilityExecutableAvailabilityStatus.Available, "Test isolation boundary available.");
    internal Exception? StartException { get; set; }
    internal int Starts { get; private set; }
    internal string? LastWorkingDirectory { get; private set; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest) => Availability;

    public Process StartIsolated(ProcessStartInfo startInfo, CapabilityArtifactManifest manifest, ICapabilityExecutableArtifactLease artifactLease)
    {
        if (StartException is not null)
        {
            throw StartException;
        }
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Test process could not start.");
        }
        Starts++;
        LastWorkingDirectory = startInfo.WorkingDirectory;
        Started.TrySetResult();
        return process;
    }
}
