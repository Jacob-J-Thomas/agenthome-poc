using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputRouteIntentSourceTestDouble(HumanInputRouteIntentSourceResult result) : IHumanInputRouteIntentSource
{
    internal HumanInputRequest? Request { get; private set; }

    internal Exception? ResolveException { get; set; }

    internal bool DelayResolveUntilCancellation { get; set; }

    internal TaskCompletionSource<bool>? ResolveEntered { get; set; }

    public async Task<HumanInputRouteIntentSourceResult> ResolveAsync(HumanInputRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        ResolveEntered?.TrySetResult(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (DelayResolveUntilCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (ResolveException is not null)
        {
            throw ResolveException;
        }

        return result;
    }
}
