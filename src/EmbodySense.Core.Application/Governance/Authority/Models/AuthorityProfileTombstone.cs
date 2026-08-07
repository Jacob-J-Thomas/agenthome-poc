using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Records an irreversible tombstone without deleting or changing historical profile evidence.</summary>
/// <param name="OperationId">The immutable tombstone operation identity.</param>
/// <param name="ActorId">The bounded actor reference.</param>
/// <param name="Reason">The bounded non-secret reason.</param>
/// <param name="RecordedAtUtc">The trusted tombstone time.</param>
public sealed record AuthorityProfileTombstone(string OperationId, AuthorityActorId ActorId, AuthorityPurpose Reason, DateTimeOffset RecordedAtUtc);
