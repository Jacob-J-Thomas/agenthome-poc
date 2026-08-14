using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Maps portable governed-loop value kinds to their exact schema-1 wire vocabulary.</summary>
public static class GovernedLoopValueKindVocabulary
{
    /// <summary>Returns the exact lowercase schema-1 token for a defined value kind.</summary>
    /// <param name="kind">The value kind.</param>
    /// <returns>The canonical token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="kind"/> is undefined.</exception>
    public static string ToCanonical(GovernedLoopValueKind kind)
    {
        return kind switch
        {
            GovernedLoopValueKind.Text => "text",
            GovernedLoopValueKind.Boolean => "boolean",
            GovernedLoopValueKind.Integer => "integer",
            GovernedLoopValueKind.Number => "number",
            GovernedLoopValueKind.Object => "object",
            GovernedLoopValueKind.Array => "array",
            GovernedLoopValueKind.Binary => "binary",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>Parses one exact lowercase schema-1 value-kind token without aliases.</summary>
    /// <param name="value">The candidate token.</param>
    /// <param name="kind">The parsed kind, or <see cref="GovernedLoopValueKind.Unknown"/> on failure.</param>
    /// <returns><see langword="true"/> only for an exact defined token.</returns>
    public static bool TryParse(string? value, out GovernedLoopValueKind kind)
    {
        kind = value switch
        {
            "text" => GovernedLoopValueKind.Text,
            "boolean" => GovernedLoopValueKind.Boolean,
            "integer" => GovernedLoopValueKind.Integer,
            "number" => GovernedLoopValueKind.Number,
            "object" => GovernedLoopValueKind.Object,
            "array" => GovernedLoopValueKind.Array,
            "binary" => GovernedLoopValueKind.Binary,
            _ => GovernedLoopValueKind.Unknown
        };
        return kind != GovernedLoopValueKind.Unknown;
    }
}
