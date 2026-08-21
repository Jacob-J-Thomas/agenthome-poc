using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Fences the first native syscall capable of changing an admitted workspace target state.</summary>
public interface IWorkspaceActionNativeDispatchBoundary
{
    /// <summary>Crosses the durable at-most-once boundary around one exact native commit callback.</summary>
    Task<WorkspaceActionNativeOutcome> CrossAsync(
        Func<CancellationToken, Task<WorkspaceActionNativeOutcome>> callback,
        CancellationToken cancellationToken = default);
}
