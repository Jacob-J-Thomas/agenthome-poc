using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Describes one bounded deterministic typed-value rejection.</summary>
public sealed class GovernedLoopTypedValueError
{
    private GovernedLoopTypedValueError(string code, string path, string message)
    {
        Code = code;
        Path = path;
        Message = message;
    }

    /// <summary>Gets the stable machine-readable rejection code.</summary>
    /// <value>The bounded canonical code.</value>
    public string Code { get; }
    /// <summary>Gets the JSON-relative rejection path.</summary>
    /// <value>The bounded safe path.</value>
    public string Path { get; }
    /// <summary>Gets the human-readable explanation.</summary>
    /// <value>The bounded safe message.</value>
    public string Message { get; }

    /// <summary>Creates one bounded immutable typed-value error.</summary>
    /// <param name="code">The stable lowercase machine-readable code.</param>
    /// <param name="path">The non-empty JSON-relative path.</param>
    /// <param name="message">The bounded human-readable explanation.</param>
    /// <returns>A validated immutable error.</returns>
    /// <exception cref="ArgumentException">Thrown when any field is missing, unsafe, or exceeds its schema-1 bound.</exception>
    public static GovernedLoopTypedValueError Create(string code, string path, string message)
    {
        CustomLoopArtifactIdentifier.Require(code, nameof(code), CustomLoopLimits.MaxGraphValidationErrorCodeCharacters);
        if (string.IsNullOrEmpty(path) || !GovernedLoopPureNodeTextRules.IsSafe(path, CustomLoopLimits.MaxGraphValidationErrorPathCharacters))
        {
            throw new ArgumentException("Typed-value error paths must be non-empty, bounded, normalized, and safe.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(message)
            || char.IsWhiteSpace(message[0])
            || char.IsWhiteSpace(message[^1])
            || !GovernedLoopPureNodeTextRules.IsSafe(message, CustomLoopLimits.MaxGraphValidationErrorMessageCharacters))
        {
            throw new ArgumentException("Typed-value error messages must be non-empty, bounded, normalized, and safe without boundary whitespace.", nameof(message));
        }

        return new GovernedLoopTypedValueError(code, path, message);
    }
}
