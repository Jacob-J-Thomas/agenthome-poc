using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Defines one server-registered structured actuator operation.</summary>
public interface IGovernedActuatorOperation
{
    /// <summary>Gets immutable metadata pinned to one exact capability implementation.</summary>
    GovernedActuatorOperationDescriptor Descriptor { get; }

    /// <summary>Returns a bounded reason code when implementation-specific canonical input is invalid.</summary>
    string? ValidateInput(GovernedActuatorInputEvidence input);

    /// <summary>Side-effect-free preparation derives exact value-free target, precondition, and before-state evidence from canonical input.</summary>
    Task<GovernedActuatorPreparationEvidence?> PrepareAsync(
        GovernedActuatorInputEvidence input,
        CancellationToken cancellationToken = default);

    /// <summary>Executes through the service-owned irreversible boundary or proves dispatch did not start.</summary>
    Task<GovernedActuatorAdapterResult> ExecuteAsync(
        GovernedActuatorInvocation invocation,
        IGovernedActuatorDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken = default);
}
