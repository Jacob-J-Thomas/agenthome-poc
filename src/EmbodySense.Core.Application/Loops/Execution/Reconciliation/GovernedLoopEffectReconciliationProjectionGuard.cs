using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationProjectionGuard
{
    internal static string RequireIdentifier(string? value, string parameterName)
        => CustomLoopArtifactIdentifier.Require(value, parameterName, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters);

    internal static long RequirePositiveVersion(long value, string parameterName)
        => value > 0 ? value : throw new ArgumentOutOfRangeException(parameterName, "A reconciliation case version must be positive.");

    internal static string RequireSha256(string? value, string parameterName)
    {
        if (value?.Length != GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A reconciliation projection hash must be a canonical lowercase SHA-256 value.", parameterName);
        }

        return value;
    }

    internal static GovernedLoopEffectReconciliationCaseSummaryStatus RequireSummaryStatus(GovernedLoopEffectReconciliationCaseSummaryStatus value, string parameterName)
        => value != GovernedLoopEffectReconciliationCaseSummaryStatus.Unknown && Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "A reconciliation case summary requires a supported non-unknown status.");
}
