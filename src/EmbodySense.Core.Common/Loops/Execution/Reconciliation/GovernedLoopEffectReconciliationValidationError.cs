using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Describes one bounded value-free reconciliation rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopEffectReconciliationValidationError : IEquatable<GovernedLoopEffectReconciliationValidationError>
{
    private GovernedLoopEffectReconciliationValidationError(GovernedLoopEffectReconciliationValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
        Message = MessageFor(code);
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopEffectReconciliationValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative field path.</summary>
    public string Path { get; }

    /// <summary>Gets a fixed value-free message derived only from the rejection category.</summary>
    public string Message { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopEffectReconciliationValidationError Create(GovernedLoopEffectReconciliationValidationErrorCode code, string path)
        => new(Enum.IsDefined(code) ? code : GovernedLoopEffectReconciliationValidationErrorCode.Required, IsSafePath(path) ? path : "$");

    /// <inheritdoc />
    public bool Equals(GovernedLoopEffectReconciliationValidationError? other)
        => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopEffectReconciliationValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
        => path.Length is > 0 and <= GovernedLoopEffectReconciliationContractLimits.MaxErrorPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static string MessageFor(GovernedLoopEffectReconciliationValidationErrorCode code)
        => code switch
        {
            GovernedLoopEffectReconciliationValidationErrorCode.Required => "Effect reconciliation rejected: a required value is absent.",
            GovernedLoopEffectReconciliationValidationErrorCode.UnsupportedSchemaVersion => "Effect reconciliation rejected: the schema version is unsupported.",
            GovernedLoopEffectReconciliationValidationErrorCode.InvalidIdentity => "Effect reconciliation rejected: an identity is not canonical and bounded.",
            GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration => "Effect reconciliation rejected: a closed category is unsupported.",
            GovernedLoopEffectReconciliationValidationErrorCode.InvalidHash => "Effect reconciliation rejected: a hash is not canonical lowercase SHA-256 evidence.",
            GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch => "Effect reconciliation rejected: retained evidence does not match immutable content.",
            GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp => "Effect reconciliation rejected: a trusted UTC boundary is invalid.",
            GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded => "Effect reconciliation rejected: a finite schema bound was exceeded.",
            GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch => "Effect reconciliation rejected: exact execution or effect coordinates differ.",
            GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition => "Effect reconciliation rejected: evidence fields do not compose.",
            GovernedLoopEffectReconciliationValidationErrorCode.NonCanonicalOrder => "Effect reconciliation rejected: a collection is not canonically ordered and unique.",
            GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition => "Effect reconciliation rejected: the disposition does not match the current assessment.",
            GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution => "Effect reconciliation rejected: the resolution does not match accepted proof.",
            _ => "Effect reconciliation rejected."
        };
}
