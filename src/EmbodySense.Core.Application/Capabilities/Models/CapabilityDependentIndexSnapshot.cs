namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Captures the deterministic content identity of every registered capability dependent.</summary>
/// <param name="Status">Whether the complete set is available.</param>
/// <param name="Hash">The canonical set hash, or an empty value when unavailable.</param>
/// <param name="Dependents">The sorted defensive dependent snapshot.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
public sealed record CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus Status, string Hash, IReadOnlyList<CapabilityDependent> Dependents, string Detail)
{
    /// <summary>Gets a defensive read-only dependent snapshot.</summary>
    public IReadOnlyList<CapabilityDependent> Dependents { get; } = Array.AsReadOnly((Dependents ?? []).ToArray());
}
