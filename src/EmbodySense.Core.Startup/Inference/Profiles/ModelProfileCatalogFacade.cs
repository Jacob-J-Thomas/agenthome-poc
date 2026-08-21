using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Maps adapter-independent profile evidence into safe surface-neutral Startup contracts.</summary>
public sealed class ModelProfileCatalogFacade : IModelProfileCatalogFacade
{
    private readonly IModelProfileDefaultSource _defaultSource;
    private readonly ModelProfileCatalogService _service;

    /// <summary>Creates a facade over caller-owned replaceable catalog and default-source ports.</summary>
    public ModelProfileCatalogFacade(ModelProfileCatalogService service, IModelProfileDefaultSource defaultSource)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _defaultSource = defaultSource ?? throw new ArgumentNullException(nameof(defaultSource));
    }

    /// <inheritdoc />
    public async Task<ModelProfileCatalogResponse> ReadAsync(
        string? startAfterId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        var page = await _service.ReadAsync(startAfterId, maximumCount, cancellationToken).ConfigureAwait(false);
        if (page.Status != ModelProfileCatalogReadStatus.Available)
        {
            return new ModelProfileCatalogResponse(Token(page.Status), [], null, null);
        }

        ModelProfileDefaultReadResult? configuredDefault;
        try
        {
            configuredDefault = await _defaultSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            configuredDefault = null;
        }
        CapabilityId? exactDefaultProfileId = null;
        if (configuredDefault?.Status == ModelProfileDefaultReadStatus.Found
            && configuredDefault.ProfileId is not null
            && IsHash(configuredDefault.SourceRevisionHash))
        {
            var exactDefault = await _service.ReadExactAsync(configuredDefault.ProfileId, cancellationToken).ConfigureAwait(false);
            var defaultItem = exactDefault.Status == ModelProfileCatalogReadStatus.Available
                ? exactDefault.Items.SingleOrDefault()
                : null;
            if (defaultItem is not null
                && defaultItem.Reason == ModelProfileAvailabilityReason.Ready
                && defaultItem.ProfileId.Equals(configuredDefault.ProfileId)
                && string.Equals(defaultItem.ProfileSourceRevisionHash, configuredDefault.SourceRevisionHash, StringComparison.Ordinal))
            {
                exactDefaultProfileId = configuredDefault.ProfileId;
            }
        }

        var profiles = page.Items.Select(Map).ToArray();
        return new ModelProfileCatalogResponse(
            "available",
            Array.AsReadOnly(profiles),
            page.NextCursor,
            exactDefaultProfileId?.Value);
    }

    /// <inheritdoc />
    public async Task<ModelProfileRoutingPreviewResponse> PreviewAsync(
        ModelProfileRoutingPreviewInput input,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidatePreview(input, out var dataClasses))
        {
            return Preview("invalid", "The routing preview intent is malformed or exceeds schema-1 bounds.");
        }

        CapabilityId? resolvedDefault = null;
        if (input.Policy.Selector.Kind == GovernedModelSelectorKind.Inherit)
        {
            var configured = await ReadConfiguredDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (configured.Status == "unavailable")
            {
                return Preview("unavailable", "The configured default could not be authenticated against the current catalog.");
            }
            if (configured.ProfileId is null)
            {
                return Preview("ineligible", "No configured default exists for this inherit policy.");
            }
            resolvedDefault = configured.ProfileId;
        }

        var candidates = input.Policy.ResolveCandidateOrder(resolvedDefault);
        if (candidates.Count == 0)
        {
            return Preview("ineligible", "The selector resolved no bounded candidate.");
        }

        var pins = new List<GovernedModelProfilePin>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var read = await _service.ReadExactAsync(candidate, cancellationToken).ConfigureAwait(false);
            var item = read.Status == ModelProfileCatalogReadStatus.Available ? read.Items.SingleOrDefault() : null;
            var pin = item is null ? null : ExactPin(item);
            if (item is null || item.Reason != ModelProfileAvailabilityReason.Ready || pin is null)
            {
                return read.Status == ModelProfileCatalogReadStatus.Unavailable
                    ? Preview("unavailable", $"Current catalog evidence for {candidate.Value} is unavailable.")
                    : Preview("ineligible", $"Current catalog evidence does not make {candidate.Value} eligible.");
            }
            if (!input.Policy.Requirements.StaticallySatisfiedBy(item.Metadata, input.RoleId, input.NodeTypeId)
                || dataClasses is not null && !input.Policy.Requirements.SatisfiedBy(item.Metadata!, dataClasses, input.RoleId, input.NodeTypeId))
            {
                return Preview("ineligible", $"Profile {candidate.Value} does not satisfy the exact role, node, capability, privacy, data, or hard-budget requirements.");
            }
            pins.Add(pin);
        }

        return new ModelProfileRoutingPreviewResponse(
            "eligible",
            "Current catalog, adapter, profile, privacy, and budget evidence satisfies the authoring intent. Graph, role, grant, capability admission, and fresh attempt authority remain mandatory.",
            input.Policy.ContentHash,
            resolvedDefault?.Value,
            pins[0],
            Array.AsReadOnly(pins.Skip(1).ToArray()),
            input.Policy.Requirements,
            true);
    }

    private static ModelProfileCatalogItemSnapshot Map(ModelProfileCatalogItem item)
        => new(
            item.ProfileId.Value,
            item.Metadata,
            Token(item.Reason),
            item.CapabilityCatalogRevision,
            item.AdapterRegistryRevisionHash,
            item.ProfileSourceRevisionHash,
            item.Reason == ModelProfileAvailabilityReason.Ready && item.Metadata is not null
                ? RecommendedPolicy(item)
                : null,
            ExactPin(item));

    private static GovernedModelProfilePin? ExactPin(ModelProfileCatalogItem item)
    {
        if (item.Reason != ModelProfileAvailabilityReason.Ready
            || item.Metadata is null
            || item.CapabilityPin is null
            || item.ProfileSourceRevisionHash is null
            || item.AdapterRegistryRevisionHash is null)
        {
            return null;
        }
        try
        {
            return GovernedModelProfilePin.Create(
                item.CapabilityPin,
                item.Metadata,
                item.ProfileSourceRevisionHash,
                item.AdapterRegistryRevisionHash);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<(string Status, CapabilityId? ProfileId)> ReadConfiguredDefaultAsync(CancellationToken cancellationToken)
    {
        ModelProfileDefaultReadResult? configured;
        try
        {
            configured = await _defaultSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ("unavailable", null);
        }
        if (configured?.Status == ModelProfileDefaultReadStatus.NotConfigured)
        {
            return ("not-configured", null);
        }
        if (configured?.Status != ModelProfileDefaultReadStatus.Found
            || configured.ProfileId is null
            || !IsHash(configured.SourceRevisionHash))
        {
            return ("unavailable", null);
        }
        var exact = await _service.ReadExactAsync(configured.ProfileId, cancellationToken).ConfigureAwait(false);
        var item = exact.Status == ModelProfileCatalogReadStatus.Available ? exact.Items.SingleOrDefault() : null;
        return item is not null
            && item.Reason == ModelProfileAvailabilityReason.Ready
            && item.ProfileId.Equals(configured.ProfileId)
            && string.Equals(item.ProfileSourceRevisionHash, configured.SourceRevisionHash, StringComparison.Ordinal)
            ? ("found", configured.ProfileId)
            : ("unavailable", null);
    }

    private static bool TryValidatePreview(ModelProfileRoutingPreviewInput? input, out IReadOnlyList<CapabilityDataClass>? dataClasses)
    {
        dataClasses = null;
        if (input is null
            || !GovernedModelContractValidator.IsValid(input.Policy)
            || !ContextualRoleId.IsValid(input.RoleId))
        {
            return false;
        }
        try
        {
            CustomLoopArtifactIdentifier.Require(input.NodeTypeId, nameof(input.NodeTypeId));
            if (input.AuthoredInputDataClasses is null)
            {
                return true;
            }
            if (input.AuthoredInputDataClasses.Count > CapabilityContractLimits.MaxDataClasses)
            {
                return false;
            }
            var parsed = new List<CapabilityDataClass>(input.AuthoredInputDataClasses.Count);
            foreach (var value in input.AuthoredInputDataClasses)
            {
                if (!CapabilityDataClass.TryParse(value, out var dataClass, out _))
                {
                    return false;
                }
                parsed.Add(dataClass!);
            }
            if (!parsed.Select(value => value.Value).SequenceEqual(parsed.Select(value => value.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || parsed.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
            {
                return false;
            }
            dataClasses = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ModelProfileRoutingPreviewResponse Preview(string status, string reason)
        => new(status, reason, null, null, null, [], null, true);

    private static GovernedModelRoutingPolicy RecommendedPolicy(ModelProfileCatalogItem item)
    {
        var metadata = item.Metadata!;
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            metadata.Privacy.Locality is GovernedModelLocality.OnDevice or GovernedModelLocality.LocalProcess
                && metadata.Privacy.Egress == EmbodySense.Core.Common.Capabilities.Models.CapabilityEgressMode.None,
            metadata.Privacy.Egress,
            metadata.Privacy.Destinations,
            metadata.Privacy.AcceptedDataClasses,
            metadata.Privacy.Regions,
            metadata.Privacy.Retention,
            metadata.Privacy.Training);
        var requirements = GovernedModelProfileRequirements.Create(
            1,
            [GovernedModelModality.Text],
            [],
            1,
            1,
            privacy,
            GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded));
        return GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Exact(item.ProfileId),
            [],
            requirements);
    }

    private static string Token<T>(T value) where T : struct, Enum
        => value.ToString().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
