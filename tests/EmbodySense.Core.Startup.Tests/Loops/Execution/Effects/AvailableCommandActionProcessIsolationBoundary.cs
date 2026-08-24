using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Clients.CommandActions.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

internal sealed class AvailableCommandActionProcessIsolationBoundary : ICommandActionProcessIsolationBoundary
{
    internal static AvailableCommandActionProcessIsolationBoundary Instance { get; } = new();

    private AvailableCommandActionProcessIsolationBoundary()
    {
    }

    public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration)
        => new(CapabilityExecutableAvailabilityStatus.Available, "The test isolation boundary is available.");

    public CommandActionIsolatedLaunchResult StartIsolated(ProcessStartInfo startInfo, CommandActionRegistration registration, ICapabilityExecutableArtifactLease artifactLease)
        => throw new NotSupportedException("Catalog tests never launch a process.");

    public Task<bool> ProveProcessTreeTerminalAsync(Process process, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Catalog tests never prove a process tree.");

    public Task<bool> TerminateAndProveProcessTreeAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Catalog tests never terminate a process tree.");
}
