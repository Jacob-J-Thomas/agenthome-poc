using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Defines the narrow governed custom-loop invocation seam used by trigger dispatch.</summary>
internal interface ITriggerCustomLoopInvoker
{
    /// <summary>Invokes one exact idempotent custom-loop request through the retained runtime gate.</summary>
    /// <param name="input">The exact loop definition, operation identity, and trigger prompt.</param>
    /// <param name="cancellationToken">A token for the governed invocation.</param>
    /// <returns>The durable custom-loop admission and execution posture.</returns>
    Task<LoopRunInvocationResponse> InvokeAsync(LoopRunInvocationInput input, CancellationToken cancellationToken = default);
}
