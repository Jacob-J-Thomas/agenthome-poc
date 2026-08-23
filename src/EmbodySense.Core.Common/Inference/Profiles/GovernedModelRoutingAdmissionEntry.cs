using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Pins one Inference node's exact primary and complete ordered eligible fallback configurations at admission.</summary>
public sealed class GovernedModelRoutingAdmissionEntry
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelRoutingAdmissionEntry(string nodeId, string nodeTypeId, string policyHash, GovernedModelProfileRequirements requirements, bool hasAuthoredInputClassification, IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDataClass> authoredInputDataClasses, GovernedModelProfilePin primary, IReadOnlyList<GovernedModelProfilePin> fallbacks)
    {
        NodeId = nodeId;
        NodeTypeId = nodeTypeId;
        PolicyHash = policyHash;
        Requirements = requirements;
        HasAuthoredInputClassification = hasAuthoredInputClassification;
        AuthoredInputDataClasses = GovernedModelContractRules.RetainSnapshot(authoredInputDataClasses, EmbodySense.Core.Common.Capabilities.CapabilityContractLimits.MaxDataClasses, nameof(authoredInputDataClasses));
        Primary = primary;
        Fallbacks = GovernedModelContractRules.RetainSnapshot(fallbacks, GovernedModelContractLimits.MaxFallbackProfiles, nameof(fallbacks));
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-routing-admission-entry.v1", WriteCanonical);
    }

    /// <summary>Gets the exact canonical Inference node ID.</summary>
    public string NodeId { get; }
    /// <summary>Gets the exact node implementation type ID.</summary>
    public string NodeTypeId { get; }
    /// <summary>Gets the authored effective routing-policy hash.</summary>
    public string PolicyHash { get; }
    /// <summary>Gets exact common constraints every retained candidate satisfied.</summary>
    public GovernedModelProfileRequirements Requirements { get; }
    /// <summary>Gets whether graph authoring supplied an exact input-data classification for admission-time validation.</summary>
    public bool HasAuthoredInputClassification { get; }
    /// <summary>Gets the canonical authored input-data classes, never runtime payload or inferred classification.</summary>
    public IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDataClass> AuthoredInputDataClasses { get; }
    /// <summary>Gets the exact admitted primary; #339 executes only this candidate.</summary>
    public GovernedModelProfilePin Primary { get; }
    /// <summary>Gets the complete ordered eligible fallback list, which #350 alone may later select.</summary>
    public IReadOnlyList<GovernedModelProfilePin> Fallbacks { get; }
    /// <summary>Gets the canonical entry hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a complete immutable routing admission entry.</summary>
    public static GovernedModelRoutingAdmissionEntry Create(int schemaVersion, string nodeId, string nodeTypeId, string policyHash, GovernedModelProfileRequirements requirements, bool hasAuthoredInputClassification, IEnumerable<EmbodySense.Core.Common.Capabilities.CapabilityDataClass> authoredInputDataClasses, GovernedModelProfilePin primary, IEnumerable<GovernedModelProfilePin> fallbacks)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(primary);
        if (!GovernedModelContractValidator.IsValid(requirements) || !GovernedModelContractValidator.IsValid(primary))
        {
            throw new ArgumentException("Routing admission requirements and primary pin must be complete canonical values.");
        }
        var fallbackValues = GovernedModelContractRules.RequireOrderedUnique(fallbacks, nameof(fallbacks), value => value.Capability.DescriptorIdentity.Id.Value, GovernedModelContractLimits.MaxFallbackProfiles);
        if (fallbackValues.Any(value => !GovernedModelContractValidator.IsValid(value)))
        {
            throw new ArgumentException("Every fallback pin must be a complete canonical value.", nameof(fallbacks));
        }
        if (fallbackValues.Any(value => value.Capability.DescriptorIdentity.Id.Equals(primary.Capability.DescriptorIdentity.Id)))
        {
            throw new ArgumentException("Fallback profile pins cannot duplicate the primary.", nameof(fallbacks));
        }

        var dataClasses = GovernedModelContractRules.RequireCanonicalSet(
            authoredInputDataClasses,
            nameof(authoredInputDataClasses),
            value => value.Value,
            maximum: EmbodySense.Core.Common.Capabilities.CapabilityContractLimits.MaxDataClasses);
        if (dataClasses.Any(value => !EmbodySense.Core.Common.Capabilities.CapabilityDataClass.TryParse(value.Value, out var parsed, out _) || !value.Equals(parsed))
            || !hasAuthoredInputClassification && dataClasses.Count != 0)
        {
            throw new ArgumentException("Authored input classifications must be explicitly present and canonical.", nameof(authoredInputDataClasses));
        }

        return new GovernedModelRoutingAdmissionEntry(
            CustomLoopArtifactIdentifier.Require(nodeId, nameof(nodeId)),
            CustomLoopArtifactIdentifier.Require(nodeTypeId, nameof(nodeTypeId)),
            GovernedModelContractRules.RequireHash(policyHash, nameof(policyHash)),
            requirements,
            hasAuthoredInputClassification,
            dataClasses,
            primary,
            fallbackValues);
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        GovernedModelContractHash.WriteStrings(writer, "authoredInputDataClasses", AuthoredInputDataClasses.Select(value => value.Value));
        GovernedModelContractHash.WriteStrings(writer, "fallbackPinHashes", Fallbacks.Select(value => value.ContentHash));
        writer.WriteBoolean("hasAuthoredInputClassification", HasAuthoredInputClassification);
        writer.WriteString("nodeId", NodeId);
        writer.WriteString("nodeTypeId", NodeTypeId);
        writer.WriteString("policyHash", PolicyHash);
        writer.WriteString("primaryPinHash", Primary.ContentHash);
        writer.WriteString("requirementsHash", Requirements.ContentHash);
        writer.WriteEndObject();
    }
}
