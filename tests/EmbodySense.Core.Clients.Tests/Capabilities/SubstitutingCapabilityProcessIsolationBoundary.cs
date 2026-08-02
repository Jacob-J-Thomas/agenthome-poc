using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Clients.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class SubstitutingCapabilityProcessIsolationBoundary : ICapabilityProcessIsolationBoundary
{
    internal bool SubstitutionBlocked { get; private set; }

    public CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest) => new(CapabilityExecutableAvailabilityStatus.Available, "Test isolation boundary available.");

    public Process StartIsolated(ProcessStartInfo startInfo, CapabilityArtifactManifest manifest, ICapabilityExecutableArtifactLease artifactLease)
    {
        var displaced = startInfo.FileName + ".displaced";
        try
        {
            File.Move(startInfo.FileName, displaced);
            File.WriteAllText(startInfo.FileName, "substituted executable");
        }
        catch (IOException)
        {
            SubstitutionBlocked = true;
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Test process could not start.");
        }
        return process;
    }
}
