using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

internal sealed class HumanInputResponseLifecycleHarness
{
    private HumanInputResponseLifecycleHarness(
        HumanInputRequest request,
        HumanInputRequestLifecycleHarness lifecycleHarness)
    {
        Request = request;
        LifecycleHarness = lifecycleHarness;
        Store = new InMemoryHumanInputResponseLifecycleStore(lifecycleHarness.Store.Snapshot(request.RequestId));
        Service = new HumanInputResponseLifecycleService(Store, Authenticator, Transaction, "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Time);
    }

    internal HumanInputRequest Request { get; }

    internal HumanInputRequestLifecycleHarness LifecycleHarness { get; }

    internal InMemoryHumanInputResponseLifecycleStore Store { get; }

    internal RecordingHumanInputResponseActorAuthenticator Authenticator { get; } = new();

    internal StubCapabilityAuthorityTransaction Transaction { get; } = new();

    internal MutableHumanInputResponseTimeProvider Time { get; } = new(HumanInputResponseLifecycleTestData.Now.AddMinutes(5));

    internal HumanInputResponseLifecycleService Service { get; }

    internal static async Task<HumanInputResponseLifecycleHarness> CreateAsync(HumanInputRequest? request = null)
    {
        request ??= HumanInputResponseLifecycleTestData.Request();
        var lifecycle = new HumanInputRequestLifecycleHarness();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(lifecycle, request);
        return new HumanInputResponseLifecycleHarness(request, lifecycle);
    }
}
