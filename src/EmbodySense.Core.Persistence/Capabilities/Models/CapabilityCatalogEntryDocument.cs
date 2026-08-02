using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityCatalogEntryDocument(
    string DescriptorJson,
    long Revision,
    CapabilityDeclarationState Declaration,
    CapabilityInstallationState Installation,
    CapabilityEnablementState Enablement,
    CapabilityHealthState Health,
    CapabilityRetirementState Retirement,
    CapabilityTrustState Trust,
    DateTimeOffset UpdatedAtUtc,
    string LastOperationId);
