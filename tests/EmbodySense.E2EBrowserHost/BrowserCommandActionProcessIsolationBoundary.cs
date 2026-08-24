using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Clients.CommandActions.Models;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.E2EBrowserHost;

/// <summary>Runs the browser test host's exact staged command artifact behind a purpose-built process boundary.</summary>
public sealed class BrowserCommandActionProcessIsolationBoundary : ICommandActionProcessIsolationBoundary
{
    /// <summary>Gets the singleton browser-test process boundary.</summary>
    public static BrowserCommandActionProcessIsolationBoundary Instance { get; } = new();

    private BrowserCommandActionProcessIsolationBoundary()
    {
    }

    /// <inheritdoc />
    public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration.Manifest.Platform.Equals(CapabilityHostRuntime.Platform)
            ? new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Available, "The controlled browser-test process boundary is available.")
            : new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Incompatible, "The browser-test command artifact targets another platform.");
    }

    /// <inheritdoc />
    public CommandActionIsolatedLaunchResult StartIsolated(ProcessStartInfo startInfo, CommandActionRegistration registration, ICapabilityExecutableArtifactLease artifactLease)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(artifactLease);
        if (!string.Equals(startInfo.FileName, artifactLease.ExecutablePath, StringComparison.Ordinal)
            || !artifactLease.ArtifactDigest.Equals(registration.Manifest.Checksum)
            || artifactLease.ActivationRevision != registration.Template.ActivationRevision
            || artifactLease.ExecutableHandle.IsInvalid
            || CheckAvailability(registration).Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            return new CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus.RejectedBeforeStart, null);
        }

        var process = new Process { StartInfo = startInfo };
        return process.Start()
            ? new CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus.Started, process)
            : new CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus.RejectedBeforeStart, null);
    }

    /// <inheritdoc />
    public Task<bool> ProveProcessTreeTerminalAsync(Process process, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(process.HasExited);
    }

    /// <inheritdoc />
    public async Task<bool> TerminateAndProveProcessTreeAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The controlled process exited between the observation and termination request.
            }
        }
        await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        return process.HasExited;
    }
}
