using EmbodySense.Core.Application.CommandActions.Models;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Allows the first launch-capable callback only after the caller's durable effect boundary is crossed.</summary>
public interface ICommandActionNativeLaunchBoundary
{
    /// <summary>Crosses the durable boundary at most once and invokes one native launch callback.</summary>
    Task<CommandActionNativeOutcome> CrossAsync(
        Func<CancellationToken, Task<CommandActionNativeOutcome>> callback,
        CancellationToken cancellationToken = default);
}
