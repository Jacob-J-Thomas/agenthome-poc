using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Authority;

/// <summary>Describes one bounded value-free effect-authority contract rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopEffectAuthorityValidationError : IEquatable<GovernedLoopEffectAuthorityValidationError>
{
    private GovernedLoopEffectAuthorityValidationError(GovernedLoopEffectAuthorityValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopEffectAuthorityValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative path without rejected values.</summary>
    public string Path { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopEffectAuthorityValidationError Create(GovernedLoopEffectAuthorityValidationErrorCode code, string path)
    {
        var supported = Enum.IsDefined(code) ? code : GovernedLoopEffectAuthorityValidationErrorCode.Required;
        return new GovernedLoopEffectAuthorityValidationError(supported, IsSafePath(path) ? path : "$");
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopEffectAuthorityValidationError? other)
        => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopEffectAuthorityValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
        => path.Length is > 0 and <= GovernedLoopEffectAuthorityContractLimits.MaxValidationPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
}
