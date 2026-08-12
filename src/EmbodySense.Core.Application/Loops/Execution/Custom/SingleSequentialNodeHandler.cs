using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Adapts one exact canonical node descriptor to a runner-owned dispatch callback.</summary>
internal sealed class SingleSequentialNodeHandler : IGovernedLoopSequentialNodeHandler
{
    private readonly Func<CancellationToken, Task<GovernedLoopSequentialNodeHandlerResult>> _dispatch;

    internal SingleSequentialNodeHandler(
        GovernedLoopNodeDescriptor descriptor,
        Func<CancellationToken, Task<GovernedLoopSequentialNodeHandlerResult>> dispatch)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public GovernedLoopNodeDescriptor Descriptor { get; }

    internal bool WasInvoked { get; private set; }

    public Task<GovernedLoopSequentialNodeHandlerResult> DispatchAsync(
        GovernedLoopSequentialNodeDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WasInvoked = true;
        return _dispatch(cancellationToken);
    }
}
