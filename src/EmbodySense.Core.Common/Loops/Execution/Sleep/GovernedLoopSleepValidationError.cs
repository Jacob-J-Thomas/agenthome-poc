using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Describes one bounded, value-free sleep/wake/coordinator contract rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopSleepValidationError : IEquatable<GovernedLoopSleepValidationError>
{
    private GovernedLoopSleepValidationError(GovernedLoopSleepValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
        Message = MessageFor(code);
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopSleepValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative field path.</summary>
    public string Path { get; }

    /// <summary>Gets a fixed value-free message derived only from the rejection category.</summary>
    public string Message { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopSleepValidationError Create(GovernedLoopSleepValidationErrorCode code, string path)
    {
        var supported = Enum.IsDefined(code) ? code : GovernedLoopSleepValidationErrorCode.Required;
        return new GovernedLoopSleepValidationError(supported, IsSafePath(path) ? path : "$");
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopSleepValidationError? other)
        => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopSleepValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
        => path.Length is > 0 and <= GovernedLoopSleepContractLimits.MaxErrorPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static string MessageFor(GovernedLoopSleepValidationErrorCode code)
        => code switch
        {
            GovernedLoopSleepValidationErrorCode.Required => "Governed-loop sleep contract rejected: a required value is absent.",
            GovernedLoopSleepValidationErrorCode.UnsupportedSchemaVersion => "Governed-loop sleep contract rejected: the schema version is unsupported.",
            GovernedLoopSleepValidationErrorCode.InvalidIdentity => "Governed-loop sleep contract rejected: an identity is not canonical and bounded.",
            GovernedLoopSleepValidationErrorCode.InvalidEnumeration => "Governed-loop sleep contract rejected: a closed category is unsupported.",
            GovernedLoopSleepValidationErrorCode.InvalidHash => "Governed-loop sleep contract rejected: a hash is not canonical lowercase SHA-256 evidence.",
            GovernedLoopSleepValidationErrorCode.IntegrityMismatch => "Governed-loop sleep contract rejected: retained evidence does not match immutable content.",
            GovernedLoopSleepValidationErrorCode.InvalidTimestamp => "Governed-loop sleep contract rejected: a trusted UTC boundary is invalid.",
            GovernedLoopSleepValidationErrorCode.LimitExceeded => "Governed-loop sleep contract rejected: a finite schema bound was exceeded.",
            GovernedLoopSleepValidationErrorCode.BindingMismatch => "Governed-loop sleep contract rejected: exact execution coordinates are inconsistent.",
            GovernedLoopSleepValidationErrorCode.InvalidComposition => "Governed-loop sleep contract rejected: evidence fields do not compose.",
            GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion => "Governed-loop sleep contract rejected: the successor version is not contiguous.",
            GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged => "Governed-loop sleep contract rejected: immutable evidence changed across a transition.",
            GovernedLoopSleepValidationErrorCode.IllegalTransition => "Governed-loop sleep contract rejected: the proposed transition is illegal.",
            _ => "Governed-loop sleep contract rejected."
        };
}
