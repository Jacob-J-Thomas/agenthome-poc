namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Pins a stable capability id, exact version, and canonical descriptor hash.
/// </summary>
/// <param name="Id">The stable capability id.</param>
/// <param name="Version">The exact capability version.</param>
/// <param name="Hash">The canonical descriptor hash.</param>
public sealed record CapabilityDescriptorIdentity(CapabilityId Id, CapabilityVersion Version, CapabilityDescriptorHash Hash)
{
    /// <summary>
    /// Creates an exact descriptor identity after validating and hashing the descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor to pin.</param>
    /// <param name="identity">The pinned identity when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the descriptor is valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(CapabilityDescriptor? descriptor, out CapabilityDescriptorIdentity? identity, out CapabilityContractValidationResult validation)
    {
        if (!CapabilityDescriptorHash.TryCompute(descriptor, out var hash, out validation))
        {
            identity = null;
            return false;
        }

        identity = new CapabilityDescriptorIdentity(descriptor!.Id, descriptor.Version, hash!);
        return true;
    }
}
