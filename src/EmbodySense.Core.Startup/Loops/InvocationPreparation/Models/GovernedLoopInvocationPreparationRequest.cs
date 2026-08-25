namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Selects one Builder-visible graph revision for server-side governed-invocation preparation.</summary>
/// <param name="GraphId">The stable graph identifier selected by the caller.</param>
/// <param name="RevisionId">The immutable revision identifier selected by the caller.</param>
/// <remarks>These identifiers select an object only. The server derives the current publication, actor, workspace, owner role, model, eligibility, and effective authority.</remarks>
public sealed record GovernedLoopInvocationPreparationRequest(string GraphId, string RevisionId);
