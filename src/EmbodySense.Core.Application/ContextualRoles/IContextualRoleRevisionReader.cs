using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Reads immutable contextual-role revisions through an implementation supplied by a later composition layer.</summary>
public interface IContextualRoleRevisionReader
{
    /// <summary>Reads one exact contextual-role revision without resolving current authority or loading instruction content.</summary>
    /// <param name="request">The exact revision request.</param>
    /// <param name="cancellationToken">A token that cancels the read before it completes.</param>
    /// <returns>A result identifying whether the exact immutable revision was found.</returns>
    Task<ContextualRoleRevisionReadResult> ReadAsync(ContextualRoleRevisionReadRequest request, CancellationToken cancellationToken = default);
}
