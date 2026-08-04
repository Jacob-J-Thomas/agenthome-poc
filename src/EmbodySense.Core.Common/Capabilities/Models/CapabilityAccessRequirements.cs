namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Declares data, egress, and secret needs without assigning authority to satisfy them.
/// </summary>
/// <param name="DataClasses">The data classifications the capability may need.</param>
/// <param name="EgressMode">The network-egress posture.</param>
/// <param name="EgressDestinations">The canonical DNS destinations required by restricted egress.</param>
/// <param name="Secrets">The secret reference names the capability may need.</param>
public sealed record CapabilityAccessRequirements(
    IReadOnlyList<CapabilityDataClass> DataClasses,
    CapabilityEgressMode EgressMode,
    IReadOnlyList<string> EgressDestinations,
    IReadOnlyList<CapabilitySecretRequirement> Secrets)
{
    /// <summary>Gets a defensive read-only snapshot of the required data classes.</summary>
    public IReadOnlyList<CapabilityDataClass> DataClasses { get; } = DataClasses is null ? null! : Array.AsReadOnly(DataClasses.ToArray());

    /// <summary>Gets a defensive read-only snapshot of the restricted egress destinations.</summary>
    public IReadOnlyList<string> EgressDestinations { get; } = EgressDestinations is null ? null! : Array.AsReadOnly(EgressDestinations.ToArray());

    /// <summary>Gets a defensive read-only snapshot of the required secret references.</summary>
    public IReadOnlyList<CapabilitySecretRequirement> Secrets { get; } = Secrets is null ? null! : Array.AsReadOnly(Secrets.ToArray());
}
