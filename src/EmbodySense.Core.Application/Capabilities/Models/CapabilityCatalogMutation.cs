using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests one idempotent optimistic catalog transition.</summary>
/// <param name="Kind">The explicit transition kind.</param>
/// <param name="OperationId">The bounded idempotency identity.</param>
/// <param name="ExpectedCatalogRevision">The caller-observed catalog revision.</param>
/// <param name="CapabilityId">The target identifier for non-declaration transitions.</param>
/// <param name="Descriptor">The source declaration for a declaration transition.</param>
public sealed record CapabilityCatalogMutation(CapabilityCatalogMutationKind Kind, string OperationId, long ExpectedCatalogRevision, CapabilityId? CapabilityId, CapabilityDescriptor? Descriptor);
