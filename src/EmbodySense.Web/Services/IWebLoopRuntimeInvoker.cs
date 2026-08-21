using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Web.Services;

/// <summary>
/// Adapts authenticated Web ownership to custom-loop invocation and explicit resume operations.
/// </summary>
public interface IWebLoopRuntimeInvoker
{
    /// <summary>
    /// Invokes an exact saved custom-loop definition for one owning browser connection.
    /// </summary>
    /// <param name="input">The validated invocation identity, definition binding, and context options.</param>
    /// <param name="ownerConnectionId">The authenticated SignalR connection that owns any resulting approval interaction.</param>
    /// <param name="cancellationToken">The token used to cancel runtime acquisition, durable admission, and the synchronously executing invocation.</param>
    /// <returns>The durable admission or rejection response.</returns>
    Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default);

    /// <summary>Invokes one exact published governed-loop revision for one owning browser connection.</summary>
    /// <param name="input">The immutable publication, authority-grant, operation, and prompt coordinates.</param>
    /// <param name="ownerConnectionId">The authenticated SignalR connection that owns any resulting approval interaction.</param>
    /// <param name="cancellationToken">The token used to cancel runtime acquisition or pre-boundary work.</param>
    /// <returns>The canonical governed admission, execution, replay, or recovery-required response.</returns>
    Task<GovernedLoopRunInvocationResponse> InvokeGovernedLoopAsync(GovernedLoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly resumes a paused custom-loop run for one owning browser connection.
    /// </summary>
    /// <param name="input">The idempotent lifecycle-control request.</param>
    /// <param name="ownerConnectionId">The authenticated SignalR connection that owns any resulting approval interaction.</param>
    /// <param name="cancellationToken">The token used to cancel the request.</param>
    /// <returns>The durable control response.</returns>
    Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default);
}
