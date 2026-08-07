namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Records the user-owned provenance for one immutable role revision.</summary>
/// <param name="AuthorId">The stable identifier of the author who supplied the revision.</param>
/// <param name="CreatedAtUtc">The non-default UTC timestamp at which the revision was created.</param>
/// <param name="RecordedAtUtc">The non-default UTC timestamp at which the provenance was recorded.</param>
public sealed record ContextualRoleProvenance(string AuthorId, DateTimeOffset CreatedAtUtc, DateTimeOffset RecordedAtUtc);
