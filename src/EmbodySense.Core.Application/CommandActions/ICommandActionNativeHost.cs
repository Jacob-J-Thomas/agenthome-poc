using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Prepares and executes exact structured command templates through a pre-launch isolation adapter.</summary>
public interface ICommandActionNativeHost
{
    /// <summary>Checks whether every registered template and platform control is currently enforceable.</summary>
    CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration);

    /// <summary>Checks whether platform controls and the exact current activated artifact are both available without starting a process.</summary>
    /// <param name="registration">The exact server-owned command registration.</param>
    /// <param name="cancellationToken">Cancels current artifact and lifecycle resolution.</param>
    /// <returns>The current fail-closed executable availability posture.</returns>
    Task<CapabilityExecutableAvailability> CheckExecutableAvailabilityAsync(CommandActionRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Resolves and retains value-free exact artifact and input evidence without starting a process.</summary>
    Task<CommandActionNativePreparation?> PrepareAsync(CommandActionRegistration registration, GovernedActuatorInputEvidence input, CancellationToken cancellationToken = default);

    /// <summary>Revalidates one exact preparation without starting a process.</summary>
    Task<bool> IsPreparationCurrentAsync(
        CommandActionRegistration registration,
        GovernedActuatorInputEvidence input,
        string targetFingerprint,
        string preconditionEvidenceHash,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default);

    /// <summary>Executes only by crossing the supplied canonical irreversible boundary at native process launch.</summary>
    Task<CommandActionNativeExecutionResult> ExecuteAsync(CommandActionNativeExecutionRequest request, ICommandActionNativeLaunchBoundary launchBoundary, CancellationToken cancellationToken = default);

    /// <summary>Authenticates a retained conclusive outcome for one crossed attempt without relaunching.</summary>
    Task<CommandActionReconciliationProbeResult> ProbeAsync(CommandActionNativeExecutionRequest request, CancellationToken cancellationToken = default);
}
