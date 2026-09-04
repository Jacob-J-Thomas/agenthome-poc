using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputRouteIntentSourceTestDouble(HumanInputRouteIntentSourceResult result) : IHumanInputRouteIntentSource
{
    internal HumanInputRequest? Request { get; private set; }

    public Task<HumanInputRouteIntentSourceResult> ResolveAsync(HumanInputRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
