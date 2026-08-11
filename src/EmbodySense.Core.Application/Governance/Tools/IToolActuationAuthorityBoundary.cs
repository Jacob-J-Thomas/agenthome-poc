using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>Owns the final authority decision and the exact bounded tool actuator continuation.</summary>
/// <remarks>
/// Implementations must invoke the supplied actuator continuation exactly once and await it before returning
/// <see cref="ToolActuationAuthorityDisposition.Direct"/>. Denied, review-required, and ambiguous decisions must never
/// invoke the continuation. The broker validates these invariants and rejects late or duplicate invocations.
/// </remarks>
public interface IToolActuationAuthorityBoundary
{
    /// <summary>Evaluates current authority and, only for a direct decision, executes the supplied actuator inside that authority boundary.</summary>
    /// <typeparam name="TResult">The private actuator result type, which is captured and interpreted by the broker.</typeparam>
    /// <param name="request">The already permission-checked and, when required, human-approved tool request.</param>
    /// <param name="executeActuatorAsync">The single-use continuation that durably records the supplied direct decision and performs the exact workspace operation.</param>
    /// <param name="cancellationToken">The cancellation token used while evaluating authority and executing the continuation.</param>
    /// <returns>The terminal authority disposition and bounded audit evidence.</returns>
    Task<ToolActuationAuthorityExecution> ExecuteAsync<TResult>(ToolRequest request, Func<ToolActuationAuthorityExecution, CancellationToken, Task<TResult>> executeActuatorAsync, CancellationToken cancellationToken = default);
}
