using System.Security.Claims;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebHumanInputAuthorityProviderTests
{
    [Fact]
    public async Task Missing_or_wrong_session_scheme_denies_both_authority_boundaries()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("wrong-scheme")) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var provider = new WebHumanInputAuthorityProvider(accessor, new HumanInputSupersedeCandidateRegistry());
        var lifecycle = await provider.AuthorizeLifecycleAsync(new AgentRuntimeHumanInputLifecycleAuthorizationRequest("op", "hash", HumanInputRequestLifecycleOperationKind.Reject, "request", 1, "workspace", DateTimeOffset.UtcNow));
        var response = await provider.AuthenticateResponseAsync(new AgentRuntimeHumanInputResponseAuthenticationRequest("op", "hash", HumanInputResponseOperationKind.Submit, "request", "workspace", DateTimeOffset.UtcNow));

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Denied, lifecycle.Status);
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Denied, response.Status);
    }

    [Fact]
    public async Task Authenticated_session_pins_the_canonical_web_actor_and_rejects_unprepared_supersede()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var provider = new WebHumanInputAuthorityProvider(accessor, new HumanInputSupersedeCandidateRegistry(), "/tmp/agenthome-web-authority-test");
        var lifecycle = await provider.AuthorizeLifecycleAsync(new AgentRuntimeHumanInputLifecycleAuthorizationRequest("op", "hash", HumanInputRequestLifecycleOperationKind.Reject, "request", 1, "workspace", DateTimeOffset.UtcNow));
        var terms = await provider.ResolveLifecycleTermsAsync(new AgentRuntimeHumanInputLifecycleTermsRequest("op", HumanInputRequestLifecycleOperationKind.Reject, "request", 1, HumanInputRequestLifecycleStatus.Pending, null, null, "reason"));
        var supersede = await provider.ResolveLifecycleTermsAsync(new AgentRuntimeHumanInputLifecycleTermsRequest("op", HumanInputRequestLifecycleOperationKind.Supersede, "request", 1, HumanInputRequestLifecycleStatus.Pending, null, "missing", "reason"));

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Ready, lifecycle.Status);
        Assert.Equal(WorkspaceActors.Web, lifecycle.ActorId!.Value);
        Assert.False(string.IsNullOrWhiteSpace(lifecycle.AuthorityEvidenceHash));
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Ready, terms.Status);
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Unavailable, supersede.Status);
    }
}
