using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Submits a persistence-agnostic immutable revision mutation with explicit optimistic-concurrency identity.</summary>
/// <param name="Revision">The proposed immutable revision snapshot.</param>
/// <param name="ExpectedPreviousIdentity">The exact expected predecessor, or <see langword="null"/> only for a first revision.</param>
public sealed record ContextualRoleRevisionMutationRequest(ContextualRoleRevision Revision, ContextualRoleRevisionIdentity? ExpectedPreviousIdentity);
