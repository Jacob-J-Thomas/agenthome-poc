namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Echoes a server-derived preview while selecting the exact Builder-visible graph revision to confirm.</summary>
/// <param name="GraphId">The stable graph identifier selected by the caller.</param>
/// <param name="RevisionId">The immutable revision identifier selected by the caller.</param>
/// <param name="ExpectedPreviewHash">The semantic hash returned by the server preview.</param>
/// <param name="OperationId">The caller-held durable idempotency identity; it contains no authority data.</param>
public sealed record GovernedLoopInvocationAuthorityConfirmation(
    string GraphId,
    string RevisionId,
    string ExpectedPreviewHash,
    string OperationId);
