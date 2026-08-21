using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring;

internal sealed class CurrentRoleBoundRevisionActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
{
    private const string EvidenceDomain = "embodysense-current-role-bound-graph-authoring-v1";
    private readonly AuthorityActorId _actorId;
    private readonly IGovernedLoopAuthoritySnapshotProvider _authority;
    private readonly ContextualRoleRevisionPin _owningRole;
    private readonly string _surfaceId;
    private readonly string _workspaceId;

    internal CurrentRoleBoundRevisionActorAuthorizer(
        string workspaceId,
        AuthorityActorId actorId,
        string surfaceId,
        ContextualRoleRevisionPin owningRole,
        IGovernedLoopAuthoritySnapshotProvider authority)
    {
        _workspaceId = workspaceId;
        _actorId = actorId;
        _surfaceId = surfaceId;
        _owningRole = owningRole;
        _authority = authority;
    }

    public async Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
        GovernedLoopRevisionActorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Request is null
            || !Equals(request.Request.ActorId, _actorId)
            || !string.Equals(
                SafeRequestHash(request.Request),
                request.RequestHash,
                StringComparison.Ordinal))
        {
            return Result(
                GovernedLoopRevisionActorAuthorizationStatus.Denied,
                request?.Request?.OperationId ?? string.Empty,
                request?.RequestHash ?? string.Empty,
                string.Empty);
        }

        var snapshot = await _authority.GetSnapshotAsync(_owningRole, cancellationToken).ConfigureAwait(false);
        if (snapshot is null
            || !snapshot.IsAvailable
            || !Equals(snapshot.OwningRole, _owningRole)
            || !string.Equals(snapshot.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !IsHash(snapshot.SourceEvidenceId))
        {
            return Result(
                GovernedLoopRevisionActorAuthorizationStatus.Unavailable,
                request.Request.OperationId,
                request.RequestHash,
                string.Empty);
        }

        var evidence = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            EvidenceDomain,
            _workspaceId,
            _actorId.Value,
            _surfaceId,
            request.RequestHash,
            _owningRole.Identity.RoleId,
            _owningRole.Identity.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _owningRole.ContentHash,
            snapshot.SourceEvidenceId)))).ToLowerInvariant();
        return Result(
            GovernedLoopRevisionActorAuthorizationStatus.Authorized,
            request.Request.OperationId,
            request.RequestHash,
            evidence);
    }

    private GovernedLoopRevisionActorAuthorization Result(
        GovernedLoopRevisionActorAuthorizationStatus status,
        string operationId,
        string requestHash,
        string evidenceHash)
        => new(status, operationId, requestHash, _actorId, evidenceHash);

    private static string SafeRequestHash(GovernedLoopRevisionLifecycleRequest request)
    {
        try
        {
            return GovernedLoopRevisionLifecycleRequestHash.Compute(request);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
