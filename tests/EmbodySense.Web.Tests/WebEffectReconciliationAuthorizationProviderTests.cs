using System.Security.Claims;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebEffectReconciliationAuthorizationProviderTests
{
    [Fact]
    public async Task Exact_authenticated_web_identity_is_ready_with_server_owned_actor_scope_and_evidence()
    {
        var provider = CreateProvider(new HttpContextAccessor
        {
            HttpContext = CreateContext(new ClaimsIdentity([
                new Claim(ClaimTypes.Name, "forged-actor"),
                new Claim(ClaimTypes.Role, "forged-role"),
                new Claim("scope", "forged-scope")], WebSessionAuthenticationDefaults.Scheme))
        });

        var result = await provider.AuthorizeAsync(CreateRequest());

        Assert.NotNull(result);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, result.Status);
        Assert.Equal(WorkspaceActors.Web, result.ActorId);
        Assert.Equal(WorkspaceScope, result.ScopeId);
        Assert.False(string.IsNullOrWhiteSpace(result.EvidenceHash));
        Assert.DoesNotContain("forged", result.EvidenceHash!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_context_aborted_request_and_multiple_identities_fail_closed()
    {
        var accessor = new HttpContextAccessor();
        var provider = CreateProvider(accessor);

        var missing = await provider.AuthorizeAsync(CreateRequest());
        accessor.HttpContext = CreateContext(
            new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme),
            new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme));
        var multiple = await provider.AuthorizeAsync(CreateRequest());
        var context = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme));
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;
        accessor.HttpContext = context;
        var requestAborted = await provider.AuthorizeAsync(CreateRequest());

        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, missing!.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, multiple!.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, requestAborted!.Status);
        Assert.Null(missing.ActorId);
        Assert.Null(multiple.EvidenceHash);
        Assert.Null(requestAborted.ScopeId);
    }

    [Fact]
    public async Task Wrong_authentication_scheme_is_denied_without_using_identity_claims()
    {
        var provider = CreateProvider(new HttpContextAccessor
        {
            HttpContext = CreateContext(new ClaimsIdentity([new Claim(ClaimTypes.Name, "trusted-looking")], "OtherScheme"))
        });

        var result = await provider.AuthorizeAsync(CreateRequest());

        Assert.NotNull(result);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, result.Status);
        Assert.Null(result.ActorId);
        Assert.Null(result.ScopeId);
        Assert.Null(result.EvidenceHash);
    }

    [Fact]
    public async Task Surface_or_purpose_mismatch_is_denied_even_for_an_authenticated_web_identity()
    {
        var provider = CreateProvider(new HttpContextAccessor
        {
            HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme))
        });

        var wrongSurface = await provider.AuthorizeAsync(CreateRequest(surfaceId: "cli"));
        var wrongPurpose = await provider.AuthorizeAsync(CreateRequest(purpose: "human-review"));

        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, wrongSurface!.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, wrongPurpose!.Status);
        Assert.Null(wrongSurface.EvidenceHash);
        Assert.Null(wrongPurpose.EvidenceHash);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_request_context_inspection()
    {
        var provider = CreateProvider(new HttpContextAccessor
        {
            HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme))
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.AuthorizeAsync(CreateRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Throwing_context_accessor_and_null_identity_fail_closed_without_authority()
    {
        var throwing = await new WebEffectReconciliationAuthorizationProvider(new ThrowingHttpContextAccessor()).AuthorizeAsync(CreateRequest());
        var nullIdentityAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new NullIdentityPrincipal() }
        };
        var nullIdentity = await new WebEffectReconciliationAuthorizationProvider(nullIdentityAccessor).AuthorizeAsync(CreateRequest());
        var throwingIdentitiesAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ThrowingIdentitiesPrincipal() }
        };
        var throwingIdentities = await new WebEffectReconciliationAuthorizationProvider(throwingIdentitiesAccessor).AuthorizeAsync(CreateRequest());

        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, throwing!.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, nullIdentity!.Status);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, throwingIdentities!.Status);
        Assert.Null(throwing.ActorId);
        Assert.Null(nullIdentity.ScopeId);
        Assert.Null(throwingIdentities.EvidenceHash);
    }

    private static WebEffectReconciliationAuthorizationProvider CreateProvider(IHttpContextAccessor accessor)
        => new(accessor);

    private static DefaultHttpContext CreateContext(params ClaimsIdentity[] identities)
        => new() { User = new ClaimsPrincipal(identities) };

    private static GovernedLoopEffectReconciliationAuthorizationRequest CreateRequest(string surfaceId = "web", string purpose = "effect-reconciliation")
        => new(
            WorkspaceRequestScope,
            surfaceId,
            purpose,
            new GovernedLoopEffectReconciliationCaseReference("case-one", 3, Hash('a'), Hash('b')),
            Hash('c'));

    private static string Hash(char value) => new(value, 64);

    private const string WorkspaceScope = "workspace-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WorkspaceRequestScope = "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class NullIdentityPrincipal : ClaimsPrincipal
    {
        public override IEnumerable<ClaimsIdentity> Identities => [null!];
    }

    private sealed class ThrowingIdentitiesPrincipal : ClaimsPrincipal
    {
        public override IEnumerable<ClaimsIdentity> Identities => throw new InvalidOperationException("private identities detail");
    }
}
