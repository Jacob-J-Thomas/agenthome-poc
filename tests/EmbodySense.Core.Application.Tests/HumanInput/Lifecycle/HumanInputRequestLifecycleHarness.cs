using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal sealed class HumanInputRequestLifecycleHarness
{
    internal HumanInputRequestLifecycleHarness(AuthorityGrant? grant = null)
    {
        Grant = grant ?? HumanInputRequestLifecycleTestData.Grant();
        Resolver = new RecordingAuthorityGrantResolver(HumanInputRequestLifecycleTestData.ActiveResolution(Grant));
        Service = new HumanInputRequestLifecycleService(
            Store,
            Authorizer,
            Resolver,
            Transaction,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Time);
    }

    internal AuthorityGrant Grant { get; }

    internal InMemoryHumanInputRequestLifecycleStore Store { get; } = new();

    internal RecordingHumanInputRequestLifecycleAuthorizer Authorizer { get; } = new();

    internal RecordingAuthorityGrantResolver Resolver { get; }

    internal StubCapabilityAuthorityTransaction Transaction { get; } = new();

    internal RecordingHumanInputTimeProvider Time { get; } = new(HumanInputRequestLifecycleTestData.Now);

    internal HumanInputRequestLifecycleService Service { get; }
}
