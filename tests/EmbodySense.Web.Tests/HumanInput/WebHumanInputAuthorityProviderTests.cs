using System.Security.Claims;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;
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

    [Fact]
    public async Task Extra_identity_is_unavailable_at_both_authority_boundaries()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme))
        };
        context.User.AddIdentity(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme));
        var provider = new WebHumanInputAuthorityProvider(new HttpContextAccessor { HttpContext = context }, new HumanInputSupersedeCandidateRegistry());

        var lifecycle = await provider.AuthorizeLifecycleAsync(new AgentRuntimeHumanInputLifecycleAuthorizationRequest("op", "hash", HumanInputRequestLifecycleOperationKind.Reject, "request", 1, "workspace", DateTimeOffset.UtcNow));
        var response = await provider.AuthenticateResponseAsync(new AgentRuntimeHumanInputResponseAuthenticationRequest("op", "hash", HumanInputResponseOperationKind.Submit, "request", "workspace", DateTimeOffset.UtcNow));

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Unavailable, lifecycle.Status);
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Unavailable, response.Status);
    }

    [Fact]
    public async Task Missing_or_aborted_context_is_unavailable()
    {
        var missing = new WebHumanInputAuthorityProvider(new HttpContextAccessor(), new HumanInputSupersedeCandidateRegistry());
        var missingResult = await missing.AuthorizeLifecycleAsync(new AgentRuntimeHumanInputLifecycleAuthorizationRequest("op", "hash", HumanInputRequestLifecycleOperationKind.Reject, "request", 1, "workspace", DateTimeOffset.UtcNow));
        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Unavailable, missingResult.Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token, User = new ClaimsPrincipal(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var aborted = new WebHumanInputAuthorityProvider(new HttpContextAccessor { HttpContext = context }, new HumanInputSupersedeCandidateRegistry());
        var result = await aborted.AuthenticateResponseAsync(new AgentRuntimeHumanInputResponseAuthenticationRequest("op", "hash", HumanInputResponseOperationKind.Submit, "request", "workspace", DateTimeOffset.UtcNow));

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Authenticated_session_resolves_a_valid_prepared_candidate_with_server_scope()
    {
        const string WorkspaceRoot = "/tmp/agenthome-human-input-authority";
        var workspaceId = HumanInputWebAuthority.GetWorkspaceId(WorkspaceRoot);
        var binding = new HumanInputRequestBinding(workspaceId, "governed-loop", "loop-revision-one", "node-one", "run-one", "checkpoint-one");
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time, binding);
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, binding, HumanInputPrivacyClass.Sensitive);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var now = DateTimeOffset.UtcNow;
        var registration = new HumanInputSupersedeCandidateRegistration(workspaceId, WorkspaceActors.Web, "operation-one", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, mutation.Operation.GrantReference!, now.AddMinutes(5));
        var registry = new HumanInputSupersedeCandidateRegistry();
        Assert.True(registry.TryRegister(registration, out var key));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var provider = new WebHumanInputAuthorityProvider(new HttpContextAccessor { HttpContext = context }, registry, WorkspaceRoot);

        var terms = await provider.ResolveLifecycleTermsAsync(new AgentRuntimeHumanInputLifecycleTermsRequest("operation-one", HumanInputRequestLifecycleOperationKind.Supersede, current.RequestId, 1, HumanInputRequestLifecycleStatus.Pending, HumanInputRequestStoreTestData.Reference(current), key, "reason"));

        Assert.Equal(AgentRuntimeHumanInputAuthorityStatus.Ready, terms.Status);
        Assert.Equal(candidate.RequestId, terms.CandidateRequest!.RequestId);
        Assert.Equal(registration.GrantReference, terms.GrantReference);
    }
}
