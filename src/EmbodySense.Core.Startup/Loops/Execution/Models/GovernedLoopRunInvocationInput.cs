using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Identifies one manual invocation of an exact published governed-loop revision.</summary>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="Publication">The exact immutable published revision pin.</param>
/// <param name="AuthorityGrant">The exact immutable authority-grant revision.</param>
/// <param name="InvocationPrompt">The bounded manual-trigger prompt.</param>
/// <remarks>
/// Workspace, actor, surface, role, graph payload, model, context, run, and execution-generation values are
/// server-owned and intentionally absent. Supplying a pin identifies immutable evidence but grants no authority.
/// </remarks>
public sealed record GovernedLoopRunInvocationInput(
    string OperationId,
    GovernedLoopRevisionPublicationPin Publication,
    AuthorityGrantReference AuthorityGrant,
    string InvocationPrompt);
