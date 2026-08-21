using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Describes one bounded value-free Wait contract rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopWaitValidationError : IEquatable<GovernedLoopWaitValidationError>
{
    private GovernedLoopWaitValidationError(GovernedLoopWaitValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
        Message = MessageFor(code);
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopWaitValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative field path.</summary>
    public string Path { get; }

    /// <summary>Gets a fixed value-free message derived only from the rejection category.</summary>
    public string Message { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopWaitValidationError Create(GovernedLoopWaitValidationErrorCode code, string path)
    {
        var supported = Enum.IsDefined(code) ? code : GovernedLoopWaitValidationErrorCode.Required;
        return new GovernedLoopWaitValidationError(supported, IsSafePath(path) ? path : "$");
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopWaitValidationError? other)
        => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopWaitValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
        => path.Length is > 0 and <= GovernedLoopWaitContractLimits.MaxErrorPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static string MessageFor(GovernedLoopWaitValidationErrorCode code)
        => code switch
        {
            GovernedLoopWaitValidationErrorCode.Required => "Governed-loop Wait contract rejected: a required value is absent.",
            GovernedLoopWaitValidationErrorCode.UnsupportedSchemaVersion => "Governed-loop Wait contract rejected: the schema version is unsupported.",
            GovernedLoopWaitValidationErrorCode.InvalidDescriptor => "Governed-loop Wait contract rejected: the node descriptor is unsupported.",
            GovernedLoopWaitValidationErrorCode.InvalidParameter => "Governed-loop Wait contract rejected: descriptor parameters are not exact.",
            GovernedLoopWaitValidationErrorCode.InvalidIdentity => "Governed-loop Wait contract rejected: an identity is not canonical and bounded.",
            GovernedLoopWaitValidationErrorCode.InvalidTimestamp => "Governed-loop Wait contract rejected: a trusted UTC boundary is invalid.",
            GovernedLoopWaitValidationErrorCode.InvalidHash => "Governed-loop Wait contract rejected: a hash is not canonical lowercase SHA-256 evidence.",
            GovernedLoopWaitValidationErrorCode.IntegrityMismatch => "Governed-loop Wait contract rejected: retained evidence does not match immutable content.",
            GovernedLoopWaitValidationErrorCode.InvalidComposition => "Governed-loop Wait contract rejected: exact evidence fields do not compose.",
            GovernedLoopWaitValidationErrorCode.BindingMismatch => "Governed-loop Wait contract rejected: exact checkpoint, wake, or frontier coordinates differ.",
            GovernedLoopWaitValidationErrorCode.LimitExceeded => "Governed-loop Wait contract rejected: a finite schema bound was exceeded.",
            GovernedLoopWaitValidationErrorCode.InvalidSuccessorVersion => "Governed-loop Wait contract rejected: continuation is not the contiguous frontier successor.",
            _ => "Governed-loop Wait contract rejected."
        };
}
