using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityCatalogOperationDocument(
    string OperationId,
    string RequestHash,
    CapabilityCatalogMutationStatus Outcome,
    long CatalogRevision,
    string CapabilityId,
    long EntryRevision,
    CapabilityDeclarationState Declaration,
    CapabilityInstallationState Installation,
    CapabilityEnablementState Enablement,
    CapabilityHealthState Health,
    CapabilityRetirementState Retirement,
    CapabilityTrustState Trust,
    DateTimeOffset UpdatedAtUtc,
    string LastOperationId);
