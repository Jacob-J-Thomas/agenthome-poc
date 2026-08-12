using EmbodySense.Core.Application.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Prepares and durably records exact governed-loop admission outcomes without dispatching execution.</summary>
public interface IGovernedLoopAdmissionService
{
    /// <summary>Admits one server-prepared exact invocation request or replays its immutable terminal outcome.</summary>
    /// <param name="request">The bounded request, or <see langword="null"/> to obtain an invalid result.</param>
    /// <param name="cancellationToken">A token honored before durable admission intent begins.</param>
    /// <returns>The exact admission, replay, rejection, conflict, or fail-closed operation result.</returns>
    Task<GovernedLoopAdmissionResult> AdmitAsync(
        GovernedLoopAdmissionRequest? request,
        CancellationToken cancellationToken = default);
}
