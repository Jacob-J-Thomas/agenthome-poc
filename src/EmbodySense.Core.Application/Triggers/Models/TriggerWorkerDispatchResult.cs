namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns the governed runner's provider-dispatch posture.</summary>
/// <param name="Outcome">The accepted, proved-rejected, or ambiguous outcome.</param>
/// <param name="Detail">The bounded inspectable result detail.</param>
/// <param name="GovernedInvocation">The exact governed admission receipt binding for accepted or terminal outcomes.</param>
public sealed record TriggerWorkerDispatchResult(TriggerDispatchOutcome Outcome, string Detail, TriggerGovernedInvocationEvidence? GovernedInvocation = null);
