using EmbodySense.Core.Common.HumanInput;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>References one exact immutable Human Input policy revision without selecting a current or default revision.</summary>
/// <param name="PolicyId">The stable policy identity.</param>
/// <param name="RevisionId">The immutable policy-revision identity.</param>
public sealed record HumanInputPolicyReference(string PolicyId, string RevisionId)
{
    private static readonly string[] _nonExactIdentifiers = ["default", "current", "latest"];

    /// <summary>Separates the policy and revision identifiers in a graph configuration reference.</summary>
    public const char Separator = '@';

    /// <summary>Parses one exact canonical <c>policy-id@revision-id</c> reference.</summary>
    /// <param name="value">The untrusted serialized policy reference.</param>
    /// <param name="reference">The parsed exact reference when valid.</param>
    /// <returns><see langword="true"/> only for one bounded non-default policy and revision identity.</returns>
    public static bool TryParse(string? value, out HumanInputPolicyReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(value) || value.Count(character => character == Separator) != 1)
        {
            return false;
        }

        var separator = value.IndexOf(Separator, StringComparison.Ordinal);
        var policyId = value[..separator];
        var revisionId = value[(separator + 1)..];
        if (!HumanInputIdentifier.IsValid(policyId)
            || !HumanInputIdentifier.IsValid(revisionId)
            || _nonExactIdentifiers.Contains(policyId, StringComparer.Ordinal)
            || _nonExactIdentifiers.Contains(revisionId, StringComparer.Ordinal))
        {
            return false;
        }

        reference = new HumanInputPolicyReference(policyId, revisionId);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{PolicyId}{Separator}{RevisionId}";
}
