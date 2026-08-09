namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Projects selected immutable evidence for a composition-owned current-state lookup.</summary>
/// <param name="DeliveryId">The delivery identity.</param>
/// <param name="LoopId">The pinned loop identity.</param>
/// <param name="LoopDefinitionVersion">The pinned loop definition version.</param>
/// <param name="LoopContentHash">The pinned loop content hash.</param>
/// <param name="AdapterCapabilityId">The pinned adapter capability identity.</param>
/// <param name="AdapterCapabilityVersion">The pinned adapter capability version.</param>
/// <param name="AdapterDescriptorHash">The pinned adapter descriptor hash.</param>
/// <param name="AdapterProviderId">The pinned adapter provider identity.</param>
/// <param name="AdapterImplementationId">The pinned adapter implementation identity.</param>
/// <param name="ActorId">The captured actor identity.</param>
/// <param name="SurfaceId">The captured surface identity.</param>
/// <param name="WorkspaceId">The captured workspace identity.</param>
/// <param name="RoleId">The captured role identity.</param>
/// <param name="AuthorityProfileId">The captured authority profile identity.</param>
/// <param name="AuthorityProfileRevision">The captured authority profile revision.</param>
public sealed record TriggerWorkerCurrentEvidenceInput(string DeliveryId, string LoopId, int LoopDefinitionVersion, string LoopContentHash, string AdapterCapabilityId, string AdapterCapabilityVersion, string AdapterDescriptorHash, string AdapterProviderId, string AdapterImplementationId, string ActorId, string SurfaceId, string WorkspaceId, string RoleId, string AuthorityProfileId, string AuthorityProfileRevision);
