using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Declares bounded candidate authority dimensions without granting authority or resolving capability availability.
/// </summary>
/// <param name="Capabilities">The exact #204 capability descriptor identities within the ceiling.</param>
/// <param name="DataClasses">The bounded data classes within the ceiling.</param>
/// <param name="MaxTargetCount">The maximum generic target count; zero permits no targets.</param>
/// <param name="MaxSideEffectClass">The maximum capability side-effect class; it does not permit an effect.</param>
/// <param name="AllowsRecurrence">Whether recurring work remains within this candidate ceiling.</param>
/// <param name="AllowsExternalPublication">Whether external publication remains within this candidate ceiling.</param>
/// <param name="AllowsIrreversibleAction">Whether irreversible action remains within this candidate ceiling.</param>
public sealed record AuthorityCeiling(
    IReadOnlyList<CapabilityDescriptorIdentity> Capabilities,
    IReadOnlyList<CapabilityDataClass> DataClasses,
    int MaxTargetCount,
    CapabilitySideEffectClass MaxSideEffectClass,
    bool AllowsRecurrence,
    bool AllowsExternalPublication,
    bool AllowsIrreversibleAction)
{
    /// <summary>Gets a defensive read-only snapshot of the exact capability identities.</summary>
    public IReadOnlyList<CapabilityDescriptorIdentity> Capabilities { get; } = Capabilities is null ? null! : Array.AsReadOnly(Capabilities.ToArray());

    /// <summary>Gets a defensive read-only snapshot of the data classes.</summary>
    public IReadOnlyList<CapabilityDataClass> DataClasses { get; } = DataClasses is null ? null! : Array.AsReadOnly(DataClasses.ToArray());
}
