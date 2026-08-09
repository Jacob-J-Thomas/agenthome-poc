using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Defines the narrow governed custom-loop invocation seam used by trigger dispatch.</summary>
internal interface ITriggerCustomLoopInvoker
{
    /// <summary>Invokes one exact idempotent custom-loop request through the retained runtime gate.</summary>
    /// <param name="input">The exact loop definition, operation identity, and trigger prompt.</param>
    /// <param name="actorContext">The exact actor, surface, workspace, and role evidence retained from the selected and revalidated trigger envelope.</param>
    /// <param name="cancellationToken">A token for the governed invocation.</param>
    /// <returns>The durable custom-loop admission and execution posture.</returns>
    Task<LoopRunInvocationResponse> InvokeAsync(LoopRunInvocationInput input, TriggerActorContext actorContext, CancellationToken cancellationToken = default);
}
