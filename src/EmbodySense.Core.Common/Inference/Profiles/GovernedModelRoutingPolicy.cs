using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines one exact bounded model-routing policy for a governed Inference node.</summary>
public sealed class GovernedModelRoutingPolicy
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelRoutingPolicy(GovernedModelRoutingSelector selector, IReadOnlyList<CapabilityId> fallbackProfileIds, GovernedModelProfileRequirements requirements)
    {
        Selector = selector;
        FallbackProfileIds = GovernedModelContractRules.RetainSnapshot(fallbackProfileIds, GovernedModelContractLimits.MaxFallbackProfiles, nameof(fallbackProfileIds));
        Requirements = requirements;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-routing-policy.v1", WriteCanonical);
    }

    /// <summary>Gets the exact or bounded-inherit primary selector.</summary>
    public GovernedModelRoutingSelector Selector { get; }
    /// <summary>Gets the authored ordered duplicate-free fallback IDs.</summary>
    /// <remarks>This issue admits these candidates but does not activate them. #350 owns fallback selection.</remarks>
    public IReadOnlyList<CapabilityId> FallbackProfileIds { get; }
    /// <summary>Gets the common constraints every primary and fallback candidate must independently satisfy.</summary>
    public GovernedModelProfileRequirements Requirements { get; }
    /// <summary>Gets the canonical routing-policy hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated immutable routing policy.</summary>
    public static GovernedModelRoutingPolicy Create(int schemaVersion, GovernedModelRoutingSelector selector, IEnumerable<CapabilityId> fallbackProfileIds, GovernedModelProfileRequirements requirements)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(requirements);
        if (!GovernedModelContractValidator.IsValid(selector) || !GovernedModelContractValidator.IsValid(requirements)
            || !Enum.IsDefined(selector.Kind) || selector.Kind == GovernedModelSelectorKind.Unknown
            || selector.Kind == GovernedModelSelectorKind.Exact && (selector.ExactProfileId is null || selector.PermittedInheritedProfileIds.Count != 0)
            || selector.Kind == GovernedModelSelectorKind.Inherit && (selector.ExactProfileId is not null || selector.PermittedInheritedProfileIds.Count == 0))
        {
            throw new ArgumentException("The routing selector is structurally invalid.", nameof(selector));
        }

        var fallbacks = GovernedModelContractRules.RequireOrderedUnique(fallbackProfileIds, nameof(fallbackProfileIds), value => value.Value, GovernedModelContractLimits.MaxFallbackProfiles);
        var primarySet = selector.Kind == GovernedModelSelectorKind.Exact ? [selector.ExactProfileId!] : selector.PermittedInheritedProfileIds;
        if (fallbacks.Any(fallback => primarySet.Contains(fallback)))
        {
            throw new ArgumentException("A fallback cannot duplicate an exact or permitted inherited primary profile.", nameof(fallbackProfileIds));
        }

        return new GovernedModelRoutingPolicy(selector, fallbacks, requirements);
    }

    /// <summary>Resolves the exact deterministic primary-first candidate order without activating a fallback.</summary>
    public IReadOnlyList<CapabilityId> ResolveCandidateOrder(CapabilityId? hostDefaultProfileId)
    {
        var primary = Selector.Resolve(hostDefaultProfileId);
        return primary is null ? Array.Empty<CapabilityId>() : Array.AsReadOnly(new[] { primary }.Concat(FallbackProfileIds).ToArray());
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        GovernedModelContractHash.WriteStrings(writer, "fallbackProfileIds", FallbackProfileIds.Select(value => value.Value));
        writer.WriteString("requirementsHash", Requirements.ContentHash);
        writer.WriteString("selectorHash", Selector.ContentHash);
        writer.WriteEndObject();
    }
}
