using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage;

/// <summary>Validates the bounded schema-1 coordinates accepted by the durable authority-usage store.</summary>
public static class GovernedLoopEffectAuthorityUsageRequestValidator
{
    /// <summary>Determines whether one request is exact, bounded, and internally consistent.</summary>
    /// <param name="request">The request to validate.</param>
    /// <returns><see langword="true"/> only for a complete schema-1 request.</returns>
    public static bool IsValid(GovernedLoopEffectAuthorityUsageRequest? request)
    {
        if (request?.Grant?.GrantId is null
            || request.Grant.Revision is null
            || request.SchemaVersion != GovernedLoopEffectAuthorityUsageRequest.CurrentSchemaVersion
            || !IsGrantHash(request.Grant.ContentHash)
            || request.CompletionConstraint is not (AuthorityGrantCompletionConstraintKind.None or AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion)
            || !IsLowerHash(request.AdmissionReceiptHash)
            || !IsIdentifier(request.RunId)
            || request.ExecutionGeneration is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxExecutionGeneration
            || !IsIdentifier(request.NodeId)
            || request.NodeAttempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt
            || !IsIdentifier(request.EffectOperationId)
            || !Enum.IsDefined(request.BoundaryKind)
            || request.BoundaryKind == 0
            || request.MaxTargetCount is < 0 or > AuthorityContractLimits.MaxTargetCount
            || request.EvaluatedAtUtc == default
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var targetBoundary = request.BoundaryKind is GovernedLoopEffectBoundaryKind.WorkspaceToolIntake
            or GovernedLoopEffectBoundaryKind.WorkspaceActuation
            or GovernedLoopEffectBoundaryKind.ConversationPublication;
        return targetBoundary
            ? request.MaxTargetCount > 0 && IsLowerHash(request.TargetFingerprint)
            : request.TargetFingerprint is null;
    }

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);

    private static bool IsGrantHash(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && IsLowerHash(value[7..]);

    private static bool IsLowerHash(string? value)
        => value is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
