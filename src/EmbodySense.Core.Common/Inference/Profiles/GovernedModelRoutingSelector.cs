using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines an exact primary profile or a host default bounded by explicit permitted profile IDs.</summary>
public sealed class GovernedModelRoutingSelector
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelRoutingSelector(GovernedModelSelectorKind kind, CapabilityId? exactProfileId, IReadOnlyList<CapabilityId> permittedInheritedProfileIds)
    {
        Kind = kind;
        ExactProfileId = exactProfileId;
        PermittedInheritedProfileIds = GovernedModelContractRules.RetainSnapshot(permittedInheritedProfileIds, GovernedModelContractLimits.MaxSetValues, nameof(permittedInheritedProfileIds));
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-routing-selector.v1", WriteCanonical);
    }

    /// <summary>Gets the selector kind.</summary>
    public GovernedModelSelectorKind Kind { get; }
    /// <summary>Gets the exact primary profile when <see cref="Kind"/> is <see cref="GovernedModelSelectorKind.Exact"/>.</summary>
    public CapabilityId? ExactProfileId { get; }
    /// <summary>Gets the canonical explicit set bounding host-default inheritance.</summary>
    public IReadOnlyList<CapabilityId> PermittedInheritedProfileIds { get; }
    /// <summary>Gets the canonical selector hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates an exact selector.</summary>
    public static GovernedModelRoutingSelector Exact(CapabilityId profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return new GovernedModelRoutingSelector(GovernedModelSelectorKind.Exact, profileId, Array.Empty<CapabilityId>());
    }

    /// <summary>Creates a bounded host-default selector.</summary>
    public static GovernedModelRoutingSelector Inherit(IEnumerable<CapabilityId> permittedProfileIds)
    {
        var values = GovernedModelContractRules.RequireCanonicalSet(permittedProfileIds, nameof(permittedProfileIds), value => value.Value, minimum: 1);
        return new GovernedModelRoutingSelector(GovernedModelSelectorKind.Inherit, null, values);
    }

    /// <summary>Resolves the primary profile without widening beyond this selector.</summary>
    /// <param name="hostDefaultProfileId">The current trusted host-default profile.</param>
    /// <returns>The exact selected profile, or <see langword="null"/> when inheritance is not permitted.</returns>
    public CapabilityId? Resolve(CapabilityId? hostDefaultProfileId)
    {
        return Kind switch
        {
            GovernedModelSelectorKind.Exact => ExactProfileId,
            GovernedModelSelectorKind.Inherit when hostDefaultProfileId is not null && PermittedInheritedProfileIds.Contains(hostDefaultProfileId) => hostDefaultProfileId,
            _ => null
        };
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("exactProfileId", ExactProfileId?.Value);
        writer.WriteNumber("kind", (int)Kind);
        GovernedModelContractHash.WriteStrings(writer, "permittedInheritedProfileIds", PermittedInheritedProfileIds.Select(value => value.Value));
        writer.WriteEndObject();
    }
}
