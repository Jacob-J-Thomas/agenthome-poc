using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Defines the persistence-agnostic boundary for submitting a validated immutable contextual-role revision mutation.</summary>
public interface IContextualRoleRevisionMutationPort
{
    /// <summary>Submits a mutation request without granting authority, interpreting source content, or binding loops.</summary>
    /// <param name="request">The requested immutable revision mutation.</param>
    /// <param name="cancellationToken">A token that cancels the mutation before it completes.</param>
    /// <returns>A structured mutation result for a later persistence implementation to produce.</returns>
    Task<ContextualRoleRevisionMutationResult> MutateAsync(ContextualRoleRevisionMutationRequest request, CancellationToken cancellationToken = default);
}
