using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines the common capability, privacy, data, role, node, and budget constraints every routed candidate must satisfy.</summary>
public sealed class GovernedModelProfileRequirements
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelProfileRequirements(IReadOnlyList<GovernedModelModality> requiredModalities, IReadOnlyList<GovernedModelCapability> requiredCapabilities, int minimumContextTokens, int minimumOutputTokens, GovernedModelPrivacyRequirement privacy, GovernedModelBudgetPolicy budget)
    {
        RequiredModalities = GovernedModelContractRules.RetainSnapshot(requiredModalities, GovernedModelContractLimits.MaxSetValues, nameof(requiredModalities));
        RequiredCapabilities = GovernedModelContractRules.RetainSnapshot(requiredCapabilities, GovernedModelContractLimits.MaxSetValues, nameof(requiredCapabilities));
        MinimumContextTokens = minimumContextTokens;
        MinimumOutputTokens = minimumOutputTokens;
        Privacy = privacy;
        Budget = budget;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-profile-requirements.v1", WriteCanonical);
    }

    /// <summary>Gets canonical required modalities.</summary>
    public IReadOnlyList<GovernedModelModality> RequiredModalities { get; }
    /// <summary>Gets canonical required inference capabilities.</summary>
    public IReadOnlyList<GovernedModelCapability> RequiredCapabilities { get; }
    /// <summary>Gets the positive minimum context window.</summary>
    public int MinimumContextTokens { get; }
    /// <summary>Gets the positive minimum maximum-output allowance.</summary>
    public int MinimumOutputTokens { get; }
    /// <summary>Gets exact privacy constraints.</summary>
    public GovernedModelPrivacyRequirement Privacy { get; }
    /// <summary>Gets exact provider-usage budget constraints.</summary>
    public GovernedModelBudgetPolicy Budget { get; }
    /// <summary>Gets the canonical requirements hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates validated immutable profile requirements.</summary>
    public static GovernedModelProfileRequirements Create(int schemaVersion, IEnumerable<GovernedModelModality> requiredModalities, IEnumerable<GovernedModelCapability> requiredCapabilities, int minimumContextTokens, int minimumOutputTokens, GovernedModelPrivacyRequirement privacy, GovernedModelBudgetPolicy budget)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(budget);
        if (!GovernedModelContractValidator.IsValid(privacy) || !GovernedModelContractValidator.IsValid(budget))
        {
            throw new ArgumentException("Profile privacy and budget requirements must be canonical.");
        }
        var modalities = GovernedModelContractRules.RequireCanonicalSet(requiredModalities, nameof(requiredModalities), value => ((int)value).ToString("D4", System.Globalization.CultureInfo.InvariantCulture), minimum: 1);
        var capabilities = GovernedModelContractRules.RequireCanonicalSet(requiredCapabilities, nameof(requiredCapabilities), value => ((int)value).ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        if (modalities.Any(value => !Enum.IsDefined(value) || value == GovernedModelModality.Unknown)
            || capabilities.Any(value => !Enum.IsDefined(value) || value == GovernedModelCapability.Unknown))
        {
            throw new ArgumentException("Required modalities and capabilities must use defined schema-1 values.");
        }

        return new GovernedModelProfileRequirements(
            modalities,
            capabilities,
            checked((int)GovernedModelContractRules.RequireQuantity(minimumContextTokens, int.MaxValue, nameof(minimumContextTokens), positive: true)),
            checked((int)GovernedModelContractRules.RequireQuantity(minimumOutputTokens, int.MaxValue, nameof(minimumOutputTokens), positive: true)),
            privacy,
            budget);
    }

    /// <summary>Returns whether exact current profile metadata independently satisfies every common requirement.</summary>
    public bool SatisfiedBy(GovernedModelProfileMetadata? metadata, IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDataClass>? actualInputDataClasses, string roleId, string nodeTypeId)
    {
        if (metadata is null || actualInputDataClasses is null)
        {
            return false;
        }

        try
        {
            var modalities = metadata.Modalities.ToHashSet();
            var capabilities = metadata.Capabilities.ToHashSet();
            return RequiredModalities.All(modalities.Contains)
                && RequiredCapabilities.All(capabilities.Contains)
                && metadata.ContextWindowTokens >= MinimumContextTokens
                && metadata.MaximumOutputTokens >= MinimumOutputTokens
                && (metadata.PermittedRoleIds.Count == 0 || metadata.PermittedRoleIds.Contains(roleId, StringComparer.Ordinal))
                && (metadata.PermittedNodeTypeIds.Count == 0 || metadata.PermittedNodeTypeIds.Contains(nodeTypeId, StringComparer.Ordinal))
                && Privacy.Satisfies(metadata.Privacy, actualInputDataClasses)
                && Budget.CanBeHardEnforcedBy(metadata.UsageSupport);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns whether profile metadata can satisfy authored policy constraints without fabricating runtime input classification.</summary>
    public bool StaticallySatisfiedBy(GovernedModelProfileMetadata? metadata, string roleId, string nodeTypeId)
    {
        if (metadata is null)
        {
            return false;
        }

        try
        {
            var modalities = metadata.Modalities.ToHashSet();
            var capabilities = metadata.Capabilities.ToHashSet();
            return RequiredModalities.All(modalities.Contains)
                && RequiredCapabilities.All(capabilities.Contains)
                && metadata.ContextWindowTokens >= MinimumContextTokens
                && metadata.MaximumOutputTokens >= MinimumOutputTokens
                && (metadata.PermittedRoleIds.Count == 0 || metadata.PermittedRoleIds.Contains(roleId, StringComparer.Ordinal))
                && (metadata.PermittedNodeTypeIds.Count == 0 || metadata.PermittedNodeTypeIds.Contains(nodeTypeId, StringComparer.Ordinal))
                && Privacy.ProfileCanSatisfy(metadata.Privacy)
                && Budget.CanBeHardEnforcedBy(metadata.UsageSupport);
        }
        catch
        {
            return false;
        }
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("budgetHash", Budget.ContentHash);
        writer.WriteNumber("minimumContextTokens", MinimumContextTokens);
        writer.WriteNumber("minimumOutputTokens", MinimumOutputTokens);
        writer.WriteString("privacyHash", Privacy.ContentHash);
        GovernedModelContractHash.WriteEnumValues(writer, "requiredCapabilities", RequiredCapabilities);
        GovernedModelContractHash.WriteEnumValues(writer, "requiredModalities", RequiredModalities);
        writer.WriteEndObject();
    }
}
