using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Describes bounded safe model-profile configuration metadata beside one exact generic capability identity.</summary>
/// <remarks>This record deliberately excludes lifecycle, trust, health, installation, endpoint, executable, environment, credential, client, and private configuration values.</remarks>
public sealed class GovernedModelProfileMetadata
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelProfileMetadata(
        CapabilityDescriptorIdentity descriptorIdentity,
        string providerId,
        string adapterId,
        string modelId,
        string adapterContractVersion,
        long configurationRevision,
        string configurationHash,
        string publicPurpose,
        IReadOnlyList<GovernedModelModality> modalities,
        IReadOnlyList<GovernedModelCapability> capabilities,
        int contextWindowTokens,
        int maximumOutputTokens,
        GovernedModelPrivacyPosture privacy,
        GovernedModelUsageSupportPolicy usageSupport,
        IReadOnlyList<string> permittedRoleIds,
        IReadOnlyList<string> permittedNodeTypeIds)
    {
        DescriptorIdentity = descriptorIdentity;
        ProviderId = providerId;
        AdapterId = adapterId;
        ModelId = modelId;
        AdapterContractVersion = adapterContractVersion;
        ConfigurationRevision = configurationRevision;
        ConfigurationHash = configurationHash;
        PublicPurpose = publicPurpose;
        Modalities = GovernedModelContractRules.RetainSnapshot(modalities, GovernedModelContractLimits.MaxSetValues, nameof(modalities));
        Capabilities = GovernedModelContractRules.RetainSnapshot(capabilities, GovernedModelContractLimits.MaxSetValues, nameof(capabilities));
        ContextWindowTokens = contextWindowTokens;
        MaximumOutputTokens = maximumOutputTokens;
        Privacy = privacy;
        UsageSupport = usageSupport;
        PermittedRoleIds = GovernedModelContractRules.RetainSnapshot(permittedRoleIds, GovernedModelContractLimits.MaxSetValues, nameof(permittedRoleIds));
        PermittedNodeTypeIds = GovernedModelContractRules.RetainSnapshot(permittedNodeTypeIds, GovernedModelContractLimits.MaxSetValues, nameof(permittedNodeTypeIds));
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-profile-metadata.v1", WriteCanonical);
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion => GovernedModelContractLimits.CurrentSchemaVersion;
    /// <summary>Gets the exact generic capability descriptor identity.</summary>
    public CapabilityDescriptorIdentity DescriptorIdentity { get; }
    /// <summary>Gets the bounded public provider identity.</summary>
    public string ProviderId { get; }
    /// <summary>Gets the bounded public adapter identity.</summary>
    public string AdapterId { get; }
    /// <summary>Gets the bounded concrete model identity.</summary>
    public string ModelId { get; }
    /// <summary>Gets the exact adapter contract version.</summary>
    public string AdapterContractVersion { get; }
    /// <summary>Gets the positive configuration revision.</summary>
    public long ConfigurationRevision { get; }
    /// <summary>Gets the hash of complete private server configuration without revealing it.</summary>
    public string ConfigurationHash { get; }
    /// <summary>Gets bounded public purpose text.</summary>
    public string PublicPurpose { get; }
    /// <summary>Gets canonical supported modalities.</summary>
    public IReadOnlyList<GovernedModelModality> Modalities { get; }
    /// <summary>Gets canonical supported inference capabilities.</summary>
    public IReadOnlyList<GovernedModelCapability> Capabilities { get; }
    /// <summary>Gets the positive context-window maximum.</summary>
    public int ContextWindowTokens { get; }
    /// <summary>Gets the positive maximum output tokens.</summary>
    public int MaximumOutputTokens { get; }
    /// <summary>Gets the safe public privacy posture.</summary>
    public GovernedModelPrivacyPosture Privacy { get; }
    /// <summary>Gets explicit authoritative-reporting and hard-enforcement support.</summary>
    public GovernedModelUsageSupportPolicy UsageSupport { get; }
    /// <summary>Gets canonical role restrictions; empty means no additional profile restriction.</summary>
    public IReadOnlyList<string> PermittedRoleIds { get; }
    /// <summary>Gets canonical node-type restrictions; empty means no additional profile restriction.</summary>
    public IReadOnlyList<string> PermittedNodeTypeIds { get; }
    /// <summary>Gets the canonical metadata hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates validated, immutable, safe profile metadata.</summary>
    public static GovernedModelProfileMetadata Create(
        int schemaVersion,
        CapabilityDescriptorIdentity descriptorIdentity,
        string providerId,
        string adapterId,
        string modelId,
        string adapterContractVersion,
        long configurationRevision,
        string configurationHash,
        string publicPurpose,
        IEnumerable<GovernedModelModality> modalities,
        IEnumerable<GovernedModelCapability> capabilities,
        int contextWindowTokens,
        int maximumOutputTokens,
        GovernedModelPrivacyPosture privacy,
        GovernedModelUsageSupportPolicy usageSupport,
        IEnumerable<string> permittedRoleIds,
        IEnumerable<string> permittedNodeTypeIds)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        RequireDescriptorIdentity(descriptorIdentity);
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(usageSupport);
        if (!GovernedModelContractValidator.IsValid(privacy) || !GovernedModelContractValidator.IsValid(usageSupport))
        {
            throw new ArgumentException("Profile privacy and usage-support evidence must be canonical.");
        }
        var canonicalModalities = GovernedModelContractRules.RequireCanonicalSet(modalities, nameof(modalities), value => ((int)value).ToString("D4", System.Globalization.CultureInfo.InvariantCulture), minimum: 1);
        var canonicalCapabilities = GovernedModelContractRules.RequireCanonicalSet(capabilities, nameof(capabilities), value => ((int)value).ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        if (canonicalModalities.Any(value => !Enum.IsDefined(value) || value == GovernedModelModality.Unknown)
            || canonicalCapabilities.Any(value => !Enum.IsDefined(value) || value == GovernedModelCapability.Unknown))
        {
            throw new ArgumentException("Model modalities and capabilities must use defined schema-1 values.");
        }

        return new GovernedModelProfileMetadata(
            descriptorIdentity,
            GovernedModelContractRules.RequireIdentifier(providerId, nameof(providerId)),
            GovernedModelContractRules.RequireIdentifier(adapterId, nameof(adapterId)),
            GovernedModelContractRules.RequireIdentifier(modelId, nameof(modelId)),
            GovernedModelContractRules.RequireIdentifier(adapterContractVersion, nameof(adapterContractVersion)),
            GovernedModelContractRules.RequireQuantity(configurationRevision, long.MaxValue, nameof(configurationRevision), positive: true),
            GovernedModelContractRules.RequireHash(configurationHash, nameof(configurationHash)),
            GovernedModelContractRules.RequirePurpose(publicPurpose, nameof(publicPurpose)),
            canonicalModalities,
            canonicalCapabilities,
            checked((int)GovernedModelContractRules.RequireQuantity(contextWindowTokens, int.MaxValue, nameof(contextWindowTokens), positive: true)),
            checked((int)GovernedModelContractRules.RequireQuantity(maximumOutputTokens, int.MaxValue, nameof(maximumOutputTokens), positive: true)),
            privacy,
            usageSupport,
            GovernedModelContractRules.RequireCanonicalSet(permittedRoleIds, nameof(permittedRoleIds), value => GovernedModelContractRules.RequireIdentifier(value, nameof(permittedRoleIds))),
            GovernedModelContractRules.RequireCanonicalSet(permittedNodeTypeIds, nameof(permittedNodeTypeIds), value => GovernedModelContractRules.RequireIdentifier(value, nameof(permittedNodeTypeIds))));
    }

    private static void RequireDescriptorIdentity(CapabilityDescriptorIdentity? identity)
    {
        if (identity?.Id is null || identity.Version is null || identity.Hash is null)
        {
            throw new ArgumentException("The exact capability descriptor identity is required.", nameof(identity));
        }

        if (!CapabilityId.TryParse(identity.Id.Value, out var id, out _)
            || !CapabilityVersion.TryParse(identity.Version.Value, out var version, out _)
            || !CapabilityDescriptorHash.TryParse(identity.Hash.Value, out var hash, out _)
            || !identity.Id.Equals(id)
            || !identity.Version.Equals(version)
            || !identity.Hash.Equals(hash))
        {
            throw new ArgumentException("The capability descriptor identity must contain exact canonical scalar values.", nameof(identity));
        }
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("adapterContractVersion", AdapterContractVersion);
        writer.WriteString("adapterId", AdapterId);
        GovernedModelContractHash.WriteEnumValues(writer, "capabilities", Capabilities);
        writer.WriteString("capabilityDescriptorHash", DescriptorIdentity.Hash.Value);
        writer.WriteString("capabilityId", DescriptorIdentity.Id.Value);
        writer.WriteString("capabilityVersion", DescriptorIdentity.Version.Value);
        writer.WriteString("configurationHash", ConfigurationHash);
        writer.WriteNumber("configurationRevision", ConfigurationRevision);
        writer.WriteNumber("contextWindowTokens", ContextWindowTokens);
        writer.WriteNumber("maximumOutputTokens", MaximumOutputTokens);
        GovernedModelContractHash.WriteEnumValues(writer, "modalities", Modalities);
        writer.WriteString("modelId", ModelId);
        GovernedModelContractHash.WriteStrings(writer, "permittedNodeTypeIds", PermittedNodeTypeIds);
        GovernedModelContractHash.WriteStrings(writer, "permittedRoleIds", PermittedRoleIds);
        writer.WriteString("privacyHash", Privacy.ContentHash);
        writer.WriteString("providerId", ProviderId);
        writer.WriteString("publicPurpose", PublicPurpose);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("usageSupportHash", UsageSupport.ContentHash);
        writer.WriteEndObject();
    }
}
