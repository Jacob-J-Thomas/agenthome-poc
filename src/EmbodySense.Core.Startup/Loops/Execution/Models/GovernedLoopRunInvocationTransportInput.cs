namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Identifies one browser or client invocation without accepting server-owned authority objects from that surface.</summary>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="Publication">The primitive exact publication coordinates.</param>
/// <param name="AuthorityGrant">The primitive exact grant coordinates.</param>
/// <param name="InvocationPrompt">The bounded manual-trigger prompt.</param>
public sealed record GovernedLoopRunInvocationTransportInput(
    string OperationId,
    GovernedLoopRevisionPublicationInput Publication,
    GovernedLoopAuthorityGrantInput AuthorityGrant,
    string InvocationPrompt);
