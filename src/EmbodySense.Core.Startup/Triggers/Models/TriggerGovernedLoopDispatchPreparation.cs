using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Returns either one exact canonical governed invocation or a proved local dispatch disposition.</summary>
/// <param name="Input">The exact trigger-namespaced canonical invocation, when prepared.</param>
/// <param name="ActorContext">The exact revalidated actor, surface, workspace, and role binding.</param>
/// <param name="Rejection">The local rejection or needs-review posture, when preparation cannot invoke.</param>
public sealed record TriggerGovernedLoopDispatchPreparation(GovernedLoopRunInvocationInput? Input, TriggerActorContext? ActorContext, TriggerWorkerDispatchResult? Rejection);
