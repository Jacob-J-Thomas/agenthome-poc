using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Represents one immutable, revision-pinned authority-grant lifecycle snapshot.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="GrantId">The stable grant identity.</param>
/// <param name="Revision">The positive immutable revision.</param>
/// <param name="PredecessorRevision">The exact predecessor revision, or null only for revision 1.</param>
/// <param name="PredecessorContentHash">The exact predecessor hash, or null only for revision 1.</param>
/// <param name="Status">The closed lifecycle posture.</param>
/// <param name="Binding">The exact profile, role, and loop publication pins.</param>
/// <param name="RequestedCeiling">The requested ceiling that later policy may only narrow.</param>
/// <param name="Boundary">The trusted-time and completion boundaries.</param>
/// <param name="ChangedByActorId">The authenticated actor retained as attribution, not authority.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
/// <param name="RecordedAtUtc">The exact trusted UTC evidence time.</param>
/// <param name="ContentHash">The canonical hash over the complete immutable snapshot excluding this field.</param>
public sealed record AuthorityGrant(
    int SchemaVersion,
    AuthorityGrantId GrantId,
    AuthorityGrantRevision Revision,
    AuthorityGrantRevision? PredecessorRevision,
    string? PredecessorContentHash,
    AuthorityGrantLifecycleStatus Status,
    AuthorityGrantBinding Binding,
    AuthorityCeiling RequestedCeiling,
    AuthorityGrantBoundary Boundary,
    AuthorityActorId ChangedByActorId,
    AuthorityPurpose Reason,
    DateTimeOffset RecordedAtUtc,
    string ContentHash);
