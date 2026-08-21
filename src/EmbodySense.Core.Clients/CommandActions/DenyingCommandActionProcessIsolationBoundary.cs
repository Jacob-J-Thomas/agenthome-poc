using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions.Models;

namespace EmbodySense.Core.Clients.CommandActions;

/// <summary>Fails closed when no registered pre-execution command isolation adapter is configured.</summary>
public sealed class DenyingCommandActionProcessIsolationBoundary : ICommandActionProcessIsolationBoundary
{
    /// <summary>Gets the reusable fail-closed boundary.</summary>
    public static DenyingCommandActionProcessIsolationBoundary Instance { get; } = new();

    private DenyingCommandActionProcessIsolationBoundary()
    {
    }

    /// <inheritdoc />
    public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration)
        => new(CapabilityExecutableAvailabilityStatus.Unavailable, "No registered pre-execution command isolation adapter is configured.");

    /// <inheritdoc />
    public CommandActionIsolatedLaunchResult StartIsolated(ProcessStartInfo startInfo, CommandActionRegistration registration, ICapabilityExecutableArtifactLease artifactLease)
        => throw new PlatformNotSupportedException("No registered pre-execution command isolation adapter is configured.");

    /// <inheritdoc />
    public Task<bool> ProveProcessTreeTerminalAsync(Process process, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> TerminateAndProveProcessTreeAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
