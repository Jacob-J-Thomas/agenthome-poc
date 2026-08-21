using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Declares the bounded public privacy posture of one server-owned model profile.</summary>
public sealed class GovernedModelPrivacyPosture
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelPrivacyPosture(GovernedModelLocality locality, CapabilityEgressMode egress, IReadOnlyList<string> destinations, IReadOnlyList<CapabilityDataClass> acceptedDataClasses, IReadOnlyList<string> regions, GovernedModelRetentionPosture retention, GovernedModelTrainingPosture training)
    {
        Locality = locality;
        Egress = egress;
        Destinations = GovernedModelContractRules.RetainSnapshot(destinations, GovernedModelContractLimits.MaxSetValues, nameof(destinations));
        AcceptedDataClasses = GovernedModelContractRules.RetainSnapshot(acceptedDataClasses, CapabilityContractLimits.MaxDataClasses, nameof(acceptedDataClasses));
        Regions = GovernedModelContractRules.RetainSnapshot(regions, GovernedModelContractLimits.MaxSetValues, nameof(regions));
        Retention = retention;
        Training = training;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-privacy-posture.v1", WriteCanonical);
    }

    /// <summary>Gets the inference locality.</summary>
    public GovernedModelLocality Locality { get; }
    /// <summary>Gets the maximum network-egress posture.</summary>
    public CapabilityEgressMode Egress { get; }
    /// <summary>Gets the canonical restricted destinations.</summary>
    public IReadOnlyList<string> Destinations { get; }
    /// <summary>Gets the canonical accepted data classes.</summary>
    public IReadOnlyList<CapabilityDataClass> AcceptedDataClasses { get; }
    /// <summary>Gets the canonical region tokens.</summary>
    public IReadOnlyList<string> Regions { get; }
    /// <summary>Gets the retention posture.</summary>
    public GovernedModelRetentionPosture Retention { get; }
    /// <summary>Gets the training-use posture.</summary>
    public GovernedModelTrainingPosture Training { get; }
    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated immutable privacy posture.</summary>
    public static GovernedModelPrivacyPosture Create(int schemaVersion, GovernedModelLocality locality, CapabilityEgressMode egress, IEnumerable<string> destinations, IEnumerable<CapabilityDataClass> acceptedDataClasses, IEnumerable<string> regions, GovernedModelRetentionPosture retention, GovernedModelTrainingPosture training)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        RequireDefined(locality, nameof(locality));
        if (!Enum.IsDefined(egress) || egress == CapabilityEgressMode.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(egress), egress, "Egress must use a known schema-1 posture.");
        }

        RequireDefined(retention, nameof(retention));
        RequireDefined(training, nameof(training));
        var canonicalDestinations = GovernedModelContractRules.RequireCanonicalSet(destinations, nameof(destinations), value => GovernedModelContractRules.RequireIdentifier(value, nameof(destinations)));
        if ((egress == CapabilityEgressMode.Restricted) != (canonicalDestinations.Count > 0)
            || egress != CapabilityEgressMode.Restricted && canonicalDestinations.Count != 0)
        {
            throw new ArgumentException("Restricted egress requires destinations and other egress modes prohibit them.", nameof(destinations));
        }

        var canonicalClasses = GovernedModelContractRules.RequireCanonicalSet(acceptedDataClasses, nameof(acceptedDataClasses), RequireDataClass, maximum: CapabilityContractLimits.MaxDataClasses);
        var canonicalRegions = GovernedModelContractRules.RequireCanonicalSet(regions, nameof(regions), value => GovernedModelContractRules.RequireIdentifier(value, nameof(regions)));
        return new GovernedModelPrivacyPosture(locality, egress, canonicalDestinations, canonicalClasses, canonicalRegions, retention, training);
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        GovernedModelContractHash.WriteStrings(writer, "acceptedDataClasses", AcceptedDataClasses.Select(value => value.Value));
        GovernedModelContractHash.WriteStrings(writer, "destinations", Destinations);
        writer.WriteNumber("egress", (int)Egress);
        writer.WriteNumber("locality", (int)Locality);
        GovernedModelContractHash.WriteStrings(writer, "regions", Regions);
        writer.WriteNumber("retention", (int)Retention);
        writer.WriteNumber("training", (int)Training);
        writer.WriteEndObject();
    }

    private static void RequireDefined<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Privacy posture values must be known schema-1 values.");
        }
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
