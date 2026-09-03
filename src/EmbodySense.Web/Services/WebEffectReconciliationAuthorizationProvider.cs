using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Workspace;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Services;

/// <summary>Adapts the authenticated local Web session to server-owned effect-reconciliation authority.</summary>
/// <remarks>
/// The provider reads the current request context for each call and never retains a principal. Claims, cookies,
/// connection identifiers, headers, browser payloads, and case content are not authority. Ready authority is pinned
/// to the canonical Web actor and the exact workspace scope supplied by Startup, with evidence derived from the
/// complete Startup request hash.
/// </remarks>
public sealed class WebEffectReconciliationAuthorizationProvider : IGovernedLoopEffectReconciliationAuthorizationProvider
{
    private const string AuthorizationPurpose = "effect-reconciliation";
    private const string ProbeAuthorizationPurpose = "effect-reconciliation.probe";
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Initializes the request-context effect-reconciliation authority adapter.</summary>
    /// <param name="httpContextAccessor">The accessor used only to inspect the current request.</param>
    /// <exception cref="ArgumentNullException">Thrown when the accessor is null.</exception>
    public WebEffectReconciliationAuthorizationProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        HttpContext? context;
        try
        {
            context = _httpContextAccessor.HttpContext;
        }
        catch
        {
            return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable));
        }

        if (context is null || context.RequestAborted.IsCancellationRequested)
        {
            return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable));
        }

        try
        {
            var identities = context.User?.Identities?.ToArray();
            if (identities is null || identities.Length == 0 || identities.Any(identity => identity is null))
            {
                return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable));
            }

            if (identities.Length != 1 || !identities[0].IsAuthenticated)
            {
                return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable));
            }

            if (!string.Equals(identities[0].AuthenticationType, WebSessionAuthenticationDefaults.Scheme, StringComparison.Ordinal))
            {
                return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Denied));
            }
        }
        catch
        {
            return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable));
        }

        if (!string.Equals(request.SurfaceId, "web", StringComparison.Ordinal)
            || !string.Equals(request.Purpose, AuthorizationPurpose, StringComparison.Ordinal)
                && !string.Equals(request.Purpose, ProbeAuthorizationPurpose, StringComparison.Ordinal))
        {
            return Task.FromResult(Closed(request, GovernedLoopEffectReconciliationAuthorizationStatus.Denied));
        }

        var scopeId = WorkspaceScope(request.WorkspaceId);
        var evidenceHash = ComputeEvidence(request, scopeId);
        return Task.FromResult(new GovernedLoopEffectReconciliationAuthorizationResult(
            GovernedLoopEffectReconciliationAuthorizationStatus.Ready,
            request.RequestHash,
            WorkspaceActors.Web,
            scopeId,
            evidenceHash));
    }

    private static GovernedLoopEffectReconciliationAuthorizationResult Closed(GovernedLoopEffectReconciliationAuthorizationRequest request, GovernedLoopEffectReconciliationAuthorizationStatus status)
        => new(status, request.RequestHash);

    private static string ComputeEvidence(GovernedLoopEffectReconciliationAuthorizationRequest request, string scopeId)
    {
        var canonical = new StringBuilder(512);
        Append(canonical, "embodysense.web.effect-reconciliation-authority.v1");
        Append(canonical, request.RequestHash);
        Append(canonical, request.WorkspaceId);
        Append(canonical, request.SurfaceId);
        Append(canonical, request.Purpose);
        Append(canonical, request.Case.CaseId);
        Append(canonical, request.Case.CaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, request.Case.ContentHash);
        Append(canonical, request.Case.BindingHash);
        Append(canonical, WorkspaceActors.Web);
        Append(canonical, scopeId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder canonical, string value)
    {
        canonical.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static string WorkspaceScope(string workspaceId)
    {
        const string Prefix = "workspace-sha256:";
        return "workspace-" + workspaceId[Prefix.Length..];
    }
}
