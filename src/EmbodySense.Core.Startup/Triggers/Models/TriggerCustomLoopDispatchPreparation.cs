using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Returns either an exact non-actuating invocation request or a proved local pre-dispatch rejection.</summary>
/// <param name="Input">The exact governed invocation input when preparation succeeded.</param>
/// <param name="Rejection">The proved local rejection when no invocation input can be produced.</param>
public sealed record TriggerCustomLoopDispatchPreparation(LoopRunInvocationInput? Input, TriggerWorkerDispatchResult? Rejection);
