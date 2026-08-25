using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation;

/// <summary>Authorizes exactly one server-recomputed invocation grant mutation and no broader authority change.</summary>
internal sealed class GovernedLoopInvocationGrantActorAuthorizer : IAuthorityGrantActorAuthorizer
{
    private readonly AuthorityGrantMutationRequest _expected;
    private readonly string _previewHash;

    public GovernedLoopInvocationGrantActorAuthorizer(AuthorityGrantMutationRequest expected, string previewHash)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _previewHash = previewHash ?? throw new ArgumentNullException(nameof(previewHash));
    }

    public Task<AuthorityGrantActorAuthorization> AuthorizeAsync(AuthorityGrantActorAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = request is not null
            && Equals(request.Request, _expected)
            && string.Equals(request.RequestHash, _expected.RequestHash, StringComparison.Ordinal)
            && request.EvaluatedAtUtc != default
            && request.EvaluatedAtUtc.Offset == TimeSpan.Zero;
        var evaluatedAtUtc = valid ? request!.EvaluatedAtUtc : DateTimeOffset.UnixEpoch;
        return Task.FromResult(new AuthorityGrantActorAuthorization(
            valid ? AuthorityGrantActorAuthorizationStatus.Authorized : AuthorityGrantActorAuthorizationStatus.Denied,
            _expected.OperationId,
            _expected.RequestHash,
            _expected.ActorId,
            evaluatedAtUtc,
            Hash("governed-loop-invocation-confirmation-v1\n" + _previewHash + "\n" + _expected.RequestHash)));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
