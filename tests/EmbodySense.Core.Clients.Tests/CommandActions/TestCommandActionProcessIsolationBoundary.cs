using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Clients.CommandActions.Models;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class TestCommandActionProcessIsolationBoundary : ICommandActionProcessIsolationBoundary
{
    internal CapabilityExecutableAvailability Availability { get; set; } = new(CapabilityExecutableAvailabilityStatus.Available, "test command isolation available");
    internal bool RejectBeforeStart { get; set; }
    internal bool TerminalProof { get; set; } = true;
    internal bool NeverCompleteProof { get; set; }
    internal int Starts { get; private set; }
    internal ProcessStartInfo? LastStartInfo { get; private set; }
    internal Func<bool>? BoundaryWasCrossed { get; set; }

    public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration) => Availability;

    public CommandActionIsolatedLaunchResult StartIsolated(ProcessStartInfo startInfo, CommandActionRegistration registration, ICapabilityExecutableArtifactLease artifactLease)
    {
        Assert.True(BoundaryWasCrossed?.Invoke());
        LastStartInfo = startInfo;
        if (RejectBeforeStart)
        {
            return new CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus.RejectedBeforeStart, null);
        }
        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Starts++;
        return new CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus.Started, process);
    }

    public Task<bool> ProveProcessTreeTerminalAsync(Process process, CancellationToken cancellationToken = default)
        => NeverCompleteProof
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task
            : Task.FromResult(TerminalProof && process.HasExited);

    public async Task<bool> TerminateAndProveProcessTreeAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (NeverCompleteProof)
        {
            return await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout);
        return TerminalProof && process.HasExited;
    }
}
