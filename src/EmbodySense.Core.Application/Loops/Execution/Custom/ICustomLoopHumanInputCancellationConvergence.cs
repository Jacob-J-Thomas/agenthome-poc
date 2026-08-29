using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Converges a durably requested custom-loop cancellation with every canonical Human Input checkpoint it owns.</summary>
/// <remarks>The parent cancellation control receipt remains the only durable coordinator. Implementations must retain
/// request-level terminal proof before allowing the caller to terminalize the run as cancelled.</remarks>
public interface ICustomLoopHumanInputCancellationConvergence
{
    /// <summary>Reconciles one cancel-requested run under its stable parent control-operation identity.</summary>
    /// <param name="run">The current canonical run observation. Implementations reread it before mutating.</param>
    /// <param name="cancellationOperationId">The durable Cancel control-operation identity that requested the run cancellation.</param>
    /// <param name="cancellationToken">The token used before any durable request-lifecycle intent begins.</param>
    /// <returns>A result that permits cancellation terminalization only after every checkpoint has safe terminal proof.</returns>
    Task<CustomLoopHumanInputCancellationConvergenceResult> ConvergeAsync(
        CustomLoopRunRecord run,
        string cancellationOperationId,
        CancellationToken cancellationToken = default);
}
