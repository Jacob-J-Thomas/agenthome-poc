using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions.Models;

namespace EmbodySense.Core.Clients.CommandActions;

/// <summary>Represents registered native infrastructure that contains one exact process tree before child code executes.</summary>
/// <remarks>Implementations must bind launch to the retained executable handle and launch fence. Attaching controls after an ordinary process start is invalid.</remarks>
public interface ICommandActionProcessIsolationBoundary
{
    /// <summary>Checks whether every declared control can be enforced for one exact registration.</summary>
    CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration);

    /// <summary>Starts the retained executable only after all controls are effective, or proves no process started.</summary>
    CommandActionIsolatedLaunchResult StartIsolated(
        ProcessStartInfo startInfo,
        CommandActionRegistration registration,
        ICapabilityExecutableArtifactLease artifactLease);

    /// <summary>Affirmatively proves that the registered contained process tree is terminal.</summary>
    Task<bool> ProveProcessTreeTerminalAsync(Process process, CancellationToken cancellationToken = default);

    /// <summary>Requests termination of the complete contained tree and proves it terminal within the supplied bound.</summary>
    Task<bool> TerminateAndProveProcessTreeAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default);
}
