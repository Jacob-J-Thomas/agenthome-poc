using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Returns either an exact non-actuating invocation request or a proved local pre-dispatch rejection.</summary>
/// <param name="Input">The exact governed invocation input when preparation succeeded.</param>
/// <param name="ActorContext">The exact trigger actor, surface, workspace, and role evidence retained from the selected envelope.</param>
/// <param name="Rejection">The proved local rejection when no invocation input can be produced.</param>
/// <remarks>The actor context remains evidence only; only the internal worker dispatcher can bind it to the governed invocation gate after durable selection and current-evidence revalidation.</remarks>
public sealed record TriggerCustomLoopDispatchPreparation(LoopRunInvocationInput? Input, TriggerActorContext? ActorContext, TriggerWorkerDispatchResult? Rejection);
