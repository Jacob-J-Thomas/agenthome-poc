using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines the maximum privacy posture and exact data classification admitted by a routing policy.</summary>
public sealed class GovernedModelPrivacyRequirement
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelPrivacyRequirement(bool localOnly, CapabilityEgressMode maximumEgress, IReadOnlyList<string> allowedDestinations, IReadOnlyList<CapabilityDataClass> allowedDataClasses, IReadOnlyList<string> allowedRegions, GovernedModelRetentionPosture maximumRetention, GovernedModelTrainingPosture maximumTraining)
    {
        LocalOnly = localOnly;
        MaximumEgress = maximumEgress;
        AllowedDestinations = GovernedModelContractRules.RetainSnapshot(allowedDestinations, GovernedModelContractLimits.MaxSetValues, nameof(allowedDestinations));
        AllowedDataClasses = GovernedModelContractRules.RetainSnapshot(allowedDataClasses, CapabilityContractLimits.MaxDataClasses, nameof(allowedDataClasses));
        AllowedRegions = GovernedModelContractRules.RetainSnapshot(allowedRegions, GovernedModelContractLimits.MaxSetValues, nameof(allowedRegions));
        MaximumRetention = maximumRetention;
        MaximumTraining = maximumTraining;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-privacy-requirement.v1", WriteCanonical);
    }

    /// <summary>Gets whether inference must remain on-device or in a local process.</summary>
    public bool LocalOnly { get; }
    /// <summary>Gets the broadest allowed egress mode.</summary>
    public CapabilityEgressMode MaximumEgress { get; }
    /// <summary>Gets the exact allowed restricted destinations.</summary>
    public IReadOnlyList<string> AllowedDestinations { get; }
    /// <summary>Gets the exact allowed input data classes.</summary>
    public IReadOnlyList<CapabilityDataClass> AllowedDataClasses { get; }
    /// <summary>Gets the allowed region tokens.</summary>
    public IReadOnlyList<string> AllowedRegions { get; }
    /// <summary>Gets the broadest permitted retention posture.</summary>
    public GovernedModelRetentionPosture MaximumRetention { get; }
    /// <summary>Gets the broadest permitted training-use posture.</summary>
    public GovernedModelTrainingPosture MaximumTraining { get; }
    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated immutable privacy requirement.</summary>
    public static GovernedModelPrivacyRequirement Create(int schemaVersion, bool localOnly, CapabilityEgressMode maximumEgress, IEnumerable<string> allowedDestinations, IEnumerable<CapabilityDataClass> allowedDataClasses, IEnumerable<string> allowedRegions, GovernedModelRetentionPosture maximumRetention, GovernedModelTrainingPosture maximumTraining)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (!Enum.IsDefined(maximumEgress) || maximumEgress == CapabilityEgressMode.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEgress), maximumEgress, "Maximum egress must use a known schema-1 posture.");
        }
        if (localOnly && maximumEgress != CapabilityEgressMode.None)
        {
            throw new ArgumentException("A local-only requirement must prohibit model-data network egress.", nameof(maximumEgress));
        }

        if (!Enum.IsDefined(maximumRetention) || maximumRetention == GovernedModelRetentionPosture.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetention), maximumRetention, "Maximum retention must use a known schema-1 posture.");
        }

        if (!Enum.IsDefined(maximumTraining) || maximumTraining == GovernedModelTrainingPosture.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTraining), maximumTraining, "Maximum training must use a known schema-1 posture.");
        }

        var canonicalDestinations = GovernedModelContractRules.RequireCanonicalSet(allowedDestinations, nameof(allowedDestinations), value => GovernedModelContractRules.RequireIdentifier(value, nameof(allowedDestinations)));
        if (maximumEgress != CapabilityEgressMode.Restricted && canonicalDestinations.Count != 0)
        {
            throw new ArgumentException("Allowed destinations are valid only with a restricted maximum egress posture.", nameof(allowedDestinations));
        }

        var canonicalClasses = GovernedModelContractRules.RequireCanonicalSet(allowedDataClasses, nameof(allowedDataClasses), RequireDataClass, maximum: CapabilityContractLimits.MaxDataClasses);
        var canonicalRegions = GovernedModelContractRules.RequireCanonicalSet(allowedRegions, nameof(allowedRegions), value => GovernedModelContractRules.RequireIdentifier(value, nameof(allowedRegions)));
        return new GovernedModelPrivacyRequirement(localOnly, maximumEgress, canonicalDestinations, canonicalClasses, canonicalRegions, maximumRetention, maximumTraining);
    }

    /// <summary>Returns whether a profile and server-classified attempt data satisfy this requirement.</summary>
    /// <param name="profile">The server-owned current profile privacy posture.</param>
    /// <param name="actualInputDataClasses">The exact server-classified input data classes.</param>
    public bool Satisfies(GovernedModelPrivacyPosture? profile, IReadOnlyList<CapabilityDataClass>? actualInputDataClasses)
    {
        if (profile is null || actualInputDataClasses is null)
        {
            return false;
        }

        try
        {
            if (!ProfileCanSatisfy(profile))
            {
                return false;
            }

            var classified = GovernedModelContractRules.RequireCanonicalSet(actualInputDataClasses, nameof(actualInputDataClasses), RequireDataClass, maximum: CapabilityContractLimits.MaxDataClasses);
            var allowedClasses = AllowedDataClasses.ToHashSet();
            var acceptedClasses = profile.AcceptedDataClasses.ToHashSet();
            return classified.All(value => allowedClasses.Contains(value) && acceptedClasses.Contains(value));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns whether a profile posture can satisfy the authored privacy envelope without pretending it is runtime input classification.</summary>
    public bool ProfileCanSatisfy(GovernedModelPrivacyPosture? profile)
    {
        if (profile is null)
        {
            return false;
        }

        try
        {
            if (LocalOnly && (profile.Locality is not GovernedModelLocality.OnDevice and not GovernedModelLocality.LocalProcess
                    || profile.Egress != CapabilityEgressMode.None))
            {
                return false;
            }

            if (profile.Locality == GovernedModelLocality.Unknown || profile.Egress == CapabilityEgressMode.Unknown || (int)profile.Egress > (int)MaximumEgress || (int)profile.Retention > (int)MaximumRetention || (int)profile.Training > (int)MaximumTraining)
            {
                return false;
            }

            var allowedDestinations = AllowedDestinations.ToHashSet(StringComparer.Ordinal);
            if (profile.Egress == CapabilityEgressMode.Restricted && profile.Destinations.Any(value => !allowedDestinations.Contains(value)))
            {
                return false;
            }

            var allowedRegions = AllowedRegions.ToHashSet(StringComparer.Ordinal);
            return (allowedRegions.Count == 0 || profile.Regions.Count > 0 && profile.Regions.All(allowedRegions.Contains))
                && AllowedDataClasses.All(value => profile.AcceptedDataClasses.Contains(value));
        }
        catch
        {
            return false;
        }
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        GovernedModelContractHash.WriteStrings(writer, "allowedDataClasses", AllowedDataClasses.Select(value => value.Value));
        GovernedModelContractHash.WriteStrings(writer, "allowedDestinations", AllowedDestinations);
        GovernedModelContractHash.WriteStrings(writer, "allowedRegions", AllowedRegions);
        writer.WriteBoolean("localOnly", LocalOnly);
        writer.WriteNumber("maximumEgress", (int)MaximumEgress);
        writer.WriteNumber("maximumRetention", (int)MaximumRetention);
        writer.WriteNumber("maximumTraining", (int)MaximumTraining);
        writer.WriteEndObject();
    }

    private static string RequireDataClass(CapabilityDataClass value)
    {
        if (!CapabilityDataClass.TryParse(value.Value, out var parsed, out _) || !value.Equals(parsed))
        {
            throw new ArgumentException("Data classes must be exact canonical scalar values.");
        }

        return value.Value;
    }
}
