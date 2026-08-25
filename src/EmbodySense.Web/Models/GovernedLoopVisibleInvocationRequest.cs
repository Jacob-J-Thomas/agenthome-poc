namespace EmbodySense.Web.Models;

/// <summary>Contains the only browser-controlled coordinates for one visible governed graph invocation.</summary>
/// <param name="GraphId">The selected stable graph identifier.</param>
/// <param name="RevisionId">The selected immutable revision identifier.</param>
/// <param name="PreviewHash">The server preview hash to confirm, or null when selecting an existing server-advertised grant.</param>
/// <param name="GrantSelection">One non-authoritative exact selector from the current server-advertised eligible grant list, or null while confirmation creates a new least-authority grant.</param>
/// <param name="OperationId">The durable browser-held idempotency identity.</param>
/// <param name="InvocationPrompt">The bounded Manual Trigger prompt.</param>
/// <remarks>Actor, workspace, role, profile, publication, grant eligibility, effective authority, and authority ceiling remain server-owned.</remarks>
public sealed record GovernedLoopVisibleInvocationRequest(
    string GraphId,
    string RevisionId,
    string? PreviewHash,
    GovernedLoopVisibleInvocationGrantSelection? GrantSelection,
    string OperationId,
    string InvocationPrompt);
