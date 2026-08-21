using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class RecordingActuatorDispatchBoundary : ICommandActionNativeLaunchBoundary
{
    internal bool Crossed { get; private set; }
    internal int Calls { get; private set; }

    public Task<CommandActionNativeOutcome> CrossAsync(Func<CancellationToken, Task<CommandActionNativeOutcome>> callback, CancellationToken cancellationToken = default)
    {
        Calls++;
        Crossed = true;
        return callback(cancellationToken);
    }
}
