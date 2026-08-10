using System.Diagnostics;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Describes one bounded, value-free governed-loop execution contract rejection.</summary>
/// <remarks>Error messages derive only from closed categories and never interpolate rejected evidence values.</remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GovernedLoopExecutionValidationError : IEquatable<GovernedLoopExecutionValidationError>
{
    private GovernedLoopExecutionValidationError(GovernedLoopExecutionValidationErrorCode code, string path)
    {
        Code = code;
        Path = path;
        Message = MessageFor(code);
    }

    /// <summary>Gets the closed rejection category.</summary>
    public GovernedLoopExecutionValidationErrorCode Code { get; }

    /// <summary>Gets the bounded schema-relative field path.</summary>
    public string Path { get; }

    /// <summary>Gets a fixed value-free message derived only from the rejection category.</summary>
    public string Message { get; }

    private string DebuggerDisplay => ToString();

    internal static GovernedLoopExecutionValidationError Create(GovernedLoopExecutionValidationErrorCode code, string path)
    {
        var supportedCode = code != GovernedLoopExecutionValidationErrorCode.Unknown && Enum.IsDefined(code) ? code : GovernedLoopExecutionValidationErrorCode.ContractRequired;
        return new GovernedLoopExecutionValidationError(supportedCode, IsSafePath(path) ? path : "$");
    }

    /// <inheritdoc />
    public bool Equals(GovernedLoopExecutionValidationError? other) => other is not null && Code == other.Code && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopExecutionValidationError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, StringComparer.Ordinal.GetHashCode(Path));

    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}";

    private static bool IsSafePath(string path)
    {
        return path.Length is > 0 and <= GovernedLoopExecutionLimits.MaxErrorPathCharacters
            && path[0] == '$'
            && path.All(character => character is '$' or '.' or '[' or ']' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
    }

    private static string MessageFor(GovernedLoopExecutionValidationErrorCode code)
    {
        return code switch
        {
            GovernedLoopExecutionValidationErrorCode.ContractRequired => "Governed-loop execution contract rejected: a required contract is absent.",
            GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion => "Governed-loop execution contract rejected: the schema version is unsupported.",
            GovernedLoopExecutionValidationErrorCode.BindingMismatch => "Governed-loop execution contract rejected: bound evidence does not share one exact execution binding.",
            GovernedLoopExecutionValidationErrorCode.CollectionTooLarge => "Governed-loop execution contract rejected: an evidence collection exceeds its finite bound.",
            GovernedLoopExecutionValidationErrorCode.CollectionNotCanonical => "Governed-loop execution contract rejected: an evidence collection is not sorted and unique.",
            GovernedLoopExecutionValidationErrorCode.EffectOperationGenerationNotUnique => "Governed-loop execution contract rejected: effect identities must not share one operation and effect generation.",
            GovernedLoopExecutionValidationErrorCode.LifecycleFrontierMismatch => "Governed-loop execution contract rejected: lifecycle and frontier postures do not compose.",
            GovernedLoopExecutionValidationErrorCode.ReviewEvidenceRequired => "Governed-loop execution contract rejected: needs-review posture lacks retained ambiguity evidence.",
            GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved => "Governed-loop execution contract rejected: a conclusive terminal posture contains unresolved evidence.",
            GovernedLoopExecutionValidationErrorCode.EffectOriginNodeMissing => "Governed-loop execution contract rejected: an effect origin node is absent from frontier evidence.",
            GovernedLoopExecutionValidationErrorCode.EffectOriginNodeNotExecutable => "Governed-loop execution contract rejected: a node-attributed effect originates from a node posture that cannot have dispatched work.",
            GovernedLoopExecutionValidationErrorCode.ProjectionSourceMissing => "Governed-loop execution contract rejected: a projection source is not retained.",
            GovernedLoopExecutionValidationErrorCode.ProjectionEffectMismatch => "Governed-loop execution contract rejected: a projection effect reference does not match retained source evidence.",
            GovernedLoopExecutionValidationErrorCode.TimestampOutsideLifecycle => "Governed-loop execution contract rejected: evidence time lies outside the lifecycle interval.",
            GovernedLoopExecutionValidationErrorCode.IllegalTransition => "Governed-loop execution contract rejected: the proposed state transition is illegal.",
            GovernedLoopExecutionValidationErrorCode.InvalidSuccessorVersion => "Governed-loop execution contract rejected: the successor version is not contiguous.",
            GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged => "Governed-loop execution contract rejected: immutable evidence changed across a transition.",
            GovernedLoopExecutionValidationErrorCode.HistoricalEvidenceMissing => "Governed-loop execution contract rejected: previously retained evidence is absent from the successor.",
            _ => "Governed-loop execution contract rejected."
        };
    }
}
