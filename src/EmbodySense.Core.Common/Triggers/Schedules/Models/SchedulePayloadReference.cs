using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>References governed payload content by non-locator identity and exact digest without persisting payload bytes.</summary>
/// <param name="GovernedReference">The canonical <c>payload/</c> reference.</param>
/// <param name="ContentHash">The exact content digest proved by the governed payload source.</param>
public sealed record SchedulePayloadReference(string GovernedReference, CapabilityIntegrityDigest ContentHash);
