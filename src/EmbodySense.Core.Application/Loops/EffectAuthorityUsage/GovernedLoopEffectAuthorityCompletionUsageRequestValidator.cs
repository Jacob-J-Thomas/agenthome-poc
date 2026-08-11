using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Authority;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage;

/// <summary>Validates bounded schema-1 first-bound-run completion usage coordinates.</summary>
public static class GovernedLoopEffectAuthorityCompletionUsageRequestValidator
{
    /// <summary>Determines whether one completion request is exact and bounded.</summary>
    /// <param name="request">The request to validate.</param>
    /// <returns><see langword="true"/> only for a complete schema-1 request.</returns>
    public static bool IsValid(GovernedLoopEffectAuthorityCompletionUsageRequest? request)
    {
        return request?.Grant?.GrantId is not null
            && request.Grant.Revision is not null
            && request.SchemaVersion == GovernedLoopEffectAuthorityCompletionUsageRequest.CurrentSchemaVersion
            && IsGrantHash(request.Grant.ContentHash)
            && IsLowerHash(request.AdmissionReceiptHash)
            && IsIdentifier(request.RunId)
            && request.ExecutionGeneration is >= 1 and <= GovernedLoopEffectAuthorityContractLimits.MaxExecutionGeneration
            && IsIdentifier(request.CompletionOperationId)
            && request.EvaluatedAtUtc != default
            && request.EvaluatedAtUtc.Offset == TimeSpan.Zero;
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
