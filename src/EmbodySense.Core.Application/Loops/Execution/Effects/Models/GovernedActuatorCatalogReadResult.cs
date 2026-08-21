using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns a bounded deterministic active actuator-operation snapshot.</summary>
/// <param name="Status">The exact read posture.</param>
/// <param name="Operations">The sorted active server-backed operation metadata.</param>
/// <param name="Detail">The bounded non-sensitive explanation.</param>
public sealed record GovernedActuatorCatalogReadResult(
    GovernedActuatorCatalogReadStatus Status,
    IReadOnlyList<GovernedActuatorOperationDescriptor> Operations,
    string Detail)
{
    /// <summary>Gets a defensive read-only operation snapshot.</summary>
    public IReadOnlyList<GovernedActuatorOperationDescriptor> Operations { get; } = Operations is null
        ? null!
        : Array.AsReadOnly(Operations.Select(operation => operation with { }).ToArray());
}
