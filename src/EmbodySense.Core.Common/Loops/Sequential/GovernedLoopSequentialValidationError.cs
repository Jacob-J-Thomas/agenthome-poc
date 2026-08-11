using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Describes one bounded value-free sequential hand-off contract rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopSequentialValidationError : IEquatable<GovernedLoopSequentialValidationError>
{
    private GovernedLoopSequentialValidationError(GovernedLoopSequentialValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopSequentialValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative path without rejected values.</summary>
    public string Path { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopSequentialValidationError Create(GovernedLoopSequentialValidationErrorCode code, string path)
    {
        var supported = code != GovernedLoopSequentialValidationErrorCode.Unknown && Enum.IsDefined(code)
            ? code
            : GovernedLoopSequentialValidationErrorCode.Required;
        return new GovernedLoopSequentialValidationError(supported, IsSafePath(path) ? path : "$");
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopSequentialValidationError? other)
        => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopSequentialValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
        => path.Length is > 0 and <= GovernedLoopSequentialContractLimits.MaxValidationPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
}
