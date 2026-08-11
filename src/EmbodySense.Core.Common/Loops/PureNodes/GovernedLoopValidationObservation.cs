using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Contains one bounded stable code and RFC 6901 path emitted by a deterministic validation node.</summary>
public sealed class GovernedLoopValidationObservation : IEquatable<GovernedLoopValidationObservation>
{
    private GovernedLoopValidationObservation(string code, string path)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Gets the stable machine-readable observation code.</summary>
    /// <value>The canonical lowercase code.</value>
    public string Code { get; }
    /// <summary>Gets the RFC 6901 path, with an empty string naming the root value.</summary>
    /// <value>The bounded canonical pointer.</value>
    public string Path { get; }

    /// <summary>Creates one bounded validation observation.</summary>
    /// <param name="code">The stable lowercase machine-readable code.</param>
    /// <param name="path">The exact RFC 6901 path, or an empty root path.</param>
    /// <returns>An immutable observation.</returns>
    /// <exception cref="ArgumentException">Thrown when the code or pointer is not canonical and bounded.</exception>
    public static GovernedLoopValidationObservation Create(string code, string path)
    {
        CustomLoopArtifactIdentifier.Require(code, nameof(code), CustomLoopLimits.MaxGraphValidationErrorCodeCharacters);
        if (path is null || path.Length > CustomLoopLimits.MaxGraphValidationErrorPathCharacters || !IsPointer(path))
        {
            throw new ArgumentException("Validation observation paths must be bounded canonical RFC 6901 pointers.", nameof(path));
        }

        if (!GovernedLoopPureNodeTextRules.IsSafe(path, CustomLoopLimits.MaxGraphValidationErrorPathCharacters))
        {
            throw new ArgumentException("Validation observation paths must use NFC-normalized safe Unicode.", nameof(path));
        }

        return new GovernedLoopValidationObservation(code, path);
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopValidationObservation? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal) && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopValidationObservation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Code), StringComparer.Ordinal.GetHashCode(Path));

    private static bool IsPointer(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] != '/')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '~')
            {
                continue;
            }

            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                return false;
            }
        }

        return true;
    }
}
