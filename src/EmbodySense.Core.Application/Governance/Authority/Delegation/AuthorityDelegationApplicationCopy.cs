using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

internal static class AuthorityDelegationApplicationCopy
{
    internal static IReadOnlyList<TValue> Snapshot<TValue>(IReadOnlyList<TValue>? values, int maximum)
    {
        if (values is null)
        {
            return null!;
        }

        try
        {
            var declared = values.Count;
            if (declared is < 0 || declared > maximum)
            {
                return null!;
            }

            var snapshot = new List<TValue>(declared);
            foreach (var value in values)
            {
                if (snapshot.Count == maximum)
                {
                    return null!;
                }

                snapshot.Add(value);
            }

            return snapshot.Count == declared ? Array.AsReadOnly(snapshot.ToArray()) : null!;
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static AuthorityCeiling Copy(AuthorityCeiling? value)
    {
        try
        {
            if (value is null)
            {
                return null!;
            }

            var capabilities = Snapshot(value.Capabilities, AuthorityContractLimits.MaxCapabilitiesPerCeiling);
            var dataClasses = Snapshot(value.DataClasses, AuthorityContractLimits.MaxDataClassesPerCeiling);
            return capabilities is null || dataClasses is null
                ? null!
                : new AuthorityCeiling(
                    capabilities.ToArray(),
                    dataClasses.ToArray(),
                    value.MaxTargetCount,
                    value.MaxSideEffectClass,
                    value.AllowsRecurrence,
                    value.AllowsExternalPublication,
                    value.AllowsIrreversibleAction);
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static IReadOnlyList<CapabilityAdmissionPin> CopyPins(IReadOnlyList<CapabilityAdmissionPin>? values)
    {
        var snapshot = Snapshot(values, CapabilityContractLimits.MaxCapabilityAdmissionPins);
        if (snapshot is null)
        {
            return null!;
        }

        try
        {
            return Array.AsReadOnly(snapshot.Select(pin => new CapabilityAdmissionPin(
                new CapabilityDescriptorIdentity(pin.DescriptorIdentity.Id, pin.DescriptorIdentity.Version, pin.DescriptorIdentity.Hash),
                pin.Kind,
                new CapabilityImplementationIdentity(pin.Implementation.ProviderId, pin.Implementation.ImplementationId),
                new CapabilityProvenance(pin.Provenance.Kind, pin.Provenance.SourceUri, pin.Provenance.SourceRevision, pin.Provenance.Integrity),
                new CapabilityDependencyArtifactMetadata(pin.Artifact.Checksum, pin.Artifact.Signature),
                pin.SafeDescription)).ToArray());
        }
        catch (Exception)
        {
            return null!;
        }
    }
}
