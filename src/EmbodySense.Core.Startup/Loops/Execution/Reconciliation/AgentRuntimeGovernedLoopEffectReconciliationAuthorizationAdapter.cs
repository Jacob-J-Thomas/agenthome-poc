using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using AppModels = EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using SurfaceModels = EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal sealed class AgentRuntimeGovernedLoopEffectReconciliationAuthorizationAdapter : IGovernedLoopEffectReconciliationAuthorizationSource
{
    private readonly IGovernedLoopEffectReconciliationAuthorizationProvider? _provider;
    private readonly string _surfaceId;
    private readonly string _workspaceId;

    internal AgentRuntimeGovernedLoopEffectReconciliationAuthorizationAdapter(string workspaceId, string surfaceId, IGovernedLoopEffectReconciliationAuthorizationProvider? provider)
    {
        _workspaceId = GovernedLoopEffectReconciliationSurfaceGuard.WorkspaceId(workspaceId, nameof(workspaceId));
        _surfaceId = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(surfaceId, nameof(surfaceId));
        _provider = provider;
    }

    public async Task<AppModels.GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(AppModels.GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Invalid, request, null);
        }
        if (_provider is null)
        {
            return Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, request, null);
        }

        var requestHash = Hash("request", _workspaceId, _surfaceId, request.Purpose, request.Case.CaseId, request.Case.CaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Case.ContentHash, request.Case.BindingHash, request.Binding.ContentHash);
        var surfaceRequest = new SurfaceModels.GovernedLoopEffectReconciliationAuthorizationRequest(_workspaceId, _surfaceId, request.Purpose, GovernedLoopEffectReconciliationProjectionMapper.Reference(request.Case), requestHash);
        SurfaceModels.GovernedLoopEffectReconciliationAuthorizationResult? authorized;
        try
        {
            authorized = await _provider.AuthorizeAsync(surfaceRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, request, null);
        }

        if (authorized is null || !string.Equals(authorized.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt, request, null);
        }

        return authorized.Status switch
        {
            SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Ready when authorized.ActorId is not null && authorized.ScopeId is not null && authorized.EvidenceHash is not null => Result(
                AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Ready,
                request,
                Hash("authority", requestHash, authorized.ActorId, authorized.ScopeId, authorized.EvidenceHash)),
            SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Denied => Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Denied, request, null),
            SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Invalid => Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Invalid, request, null),
            SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt => Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt, request, null),
            SurfaceModels.GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable => Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, request, null),
            _ => Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt, request, null),
        };
    }

    private static AppModels.GovernedLoopEffectReconciliationAuthorizationResult Result(AppModels.GovernedLoopEffectReconciliationAuthorizationStatus status, AppModels.GovernedLoopEffectReconciliationAuthorizationRequest request, string? evidenceHash)
        => new(status, request.Purpose, request.Case, request.Binding, evidenceHash);

    private static string Hash(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.reconciliation-startup-authority.v1\n");
        builder.Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
