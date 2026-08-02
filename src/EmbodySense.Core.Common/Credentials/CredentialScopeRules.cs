using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Provides fail-closed partial ordering and intersection for credential scopes.</summary>
public static class CredentialScopeRules
{
    /// <summary>Determines whether every candidate dimension is equal to or narrower than a valid ceiling.</summary>
    public static bool IsNarrowerThanOrEqual(CredentialScope? candidate, CredentialScope? ceiling)
    {
        if (!CredentialContractValidator.Validate(candidate).IsValid || !CredentialContractValidator.Validate(ceiling).IsValid)
        {
            return false;
        }

        return Narrows(candidate!.WorkspaceId, ceiling!.WorkspaceId)
            && Narrows(candidate.RoleId, ceiling.RoleId)
            && Narrows(candidate.LoopId, ceiling.LoopId)
            && Narrows(candidate.LoopRevision, ceiling.LoopRevision)
            && Narrows(candidate.NodeId, ceiling.NodeId)
            && Narrows(candidate.Capability, ceiling.Capability)
            && Narrows(candidate.Implementation, ceiling.Implementation)
            && Narrows(candidate.Service, ceiling.Service)
            && Narrows(candidate.Target, ceiling.Target)
            && Narrows(candidate.OperationClass, ceiling.OperationClass)
            && Narrows(candidate.ActorId, ceiling.ActorId)
            && (ceiling.NotBeforeUtc is null || candidate.NotBeforeUtc >= ceiling.NotBeforeUtc)
            && (ceiling.NotAfterUtc is null || candidate.NotAfterUtc <= ceiling.NotAfterUtc);
    }

    /// <summary>Intersects two valid scopes without guessing across conflicting dimensions.</summary>
    public static bool TryIntersect(CredentialScope? left, CredentialScope? right, out CredentialScope? intersection, out CredentialContractError? error)
    {
        intersection = null;
        if (!CredentialContractValidator.Validate(left).IsValid || !CredentialContractValidator.Validate(right).IsValid)
        {
            error = CredentialContractError.Create(CredentialContractErrorCode.InvalidCredentialScope, "$");
            return false;
        }

        if (!TryIntersectValue(left!.WorkspaceId, right!.WorkspaceId, out var workspace)
            || !TryIntersectValue(left.RoleId, right.RoleId, out var role)
            || !TryIntersectValue(left.LoopId, right.LoopId, out var loop)
            || !TryIntersectValue(left.LoopRevision, right.LoopRevision, out var revision)
            || !TryIntersectValue(left.NodeId, right.NodeId, out var node)
            || !TryIntersectValue(left.Capability, right.Capability, out var capability)
            || !TryIntersectValue(left.Implementation, right.Implementation, out var implementation)
            || !TryIntersectValue(left.Service, right.Service, out var service)
            || !TryIntersectValue(left.Target, right.Target, out var target)
            || !TryIntersectValue(left.OperationClass, right.OperationClass, out var operation)
            || !TryIntersectValue(left.ActorId, right.ActorId, out var actor))
        {
            error = CredentialContractError.Create(CredentialContractErrorCode.CredentialScopeConflict, "$");
            return false;
        }

        var notBefore = Max(left.NotBeforeUtc, right.NotBeforeUtc);
        var notAfter = Min(left.NotAfterUtc, right.NotAfterUtc);
        if (notBefore is not null && notAfter is not null && notBefore >= notAfter)
        {
            error = CredentialContractError.Create(CredentialContractErrorCode.CredentialScopeTimeConflict, "$");
            return false;
        }

        intersection = new CredentialScope(workspace, role, loop, revision, node, capability, implementation, service, target, operation, actor, notBefore, notAfter);
        if (!CredentialContractValidator.Validate(intersection).IsValid || !IsNarrowerThanOrEqual(intersection, left) || !IsNarrowerThanOrEqual(intersection, right))
        {
            intersection = null;
            error = CredentialContractError.Create(CredentialContractErrorCode.AmbiguousCredentialScope, "$");
            return false;
        }

        error = null;
        return true;
    }

    private static bool Narrows<T>(T? candidate, T? ceiling) where T : class => ceiling is null || candidate is not null && Equals(candidate, ceiling);
    private static bool Narrows(long? candidate, long? ceiling) => ceiling is null || candidate == ceiling;

    private static bool TryIntersectValue<T>(T? left, T? right, out T? value) where T : class
    {
        if (left is not null && right is not null && !Equals(left, right))
        {
            value = default;
            return false;
        }

        value = left ?? right;
        return true;
    }

    private static bool TryIntersectValue(long? left, long? right, out long? value)
    {
        if (left is not null && right is not null && left != right)
        {
            value = null;
            return false;
        }

        value = left ?? right;
        return true;
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : left > right ? left : right;
    private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : left < right ? left : right;
}
