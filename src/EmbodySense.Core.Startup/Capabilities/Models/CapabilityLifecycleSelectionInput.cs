namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Captures one bounded capability lifecycle selection without accepting trusted artifact evidence.</summary>
/// <param name="OperationId">The idempotent operation identity.</param>
/// <param name="Operation">One of enable, disable, upgrade, rollback, or remove.</param>
/// <param name="CapabilityId">The canonical capability identity.</param>
/// <param name="TargetVersion">The optional exact target version for enable or upgrade.</param>
public sealed record CapabilityLifecycleSelectionInput(string OperationId, string Operation, string CapabilityId, string? TargetVersion = null);
