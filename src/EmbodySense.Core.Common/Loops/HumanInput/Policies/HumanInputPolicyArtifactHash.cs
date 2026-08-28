using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Computes and verifies canonical hashes for immutable Human Input policy revisions.</summary>
public static class HumanInputPolicyArtifactHash
{
    /// <summary>Computes the canonical policy content hash without trusting the supplied hash field.</summary>
    /// <param name="artifact">The policy artifact to hash.</param>
    /// <returns>The lowercase SHA-256 digest.</returns>
    public static string Compute(HumanInputPolicyArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var content = string.Join('\n', "embodysense-human-input-policy-v1", artifact.SchemaVersion, artifact.PolicyId ?? string.Empty, artifact.RevisionId ?? string.Empty, (int)artifact.Kind, artifact.WorkspaceId ?? string.Empty, artifact.GraphId ?? string.Empty, artifact.AuthorityActorId ?? string.Empty, artifact.ResponseWindowMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, (int)artifact.TerminalDisposition);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>Returns a policy artifact carrying its canonical content hash.</summary>
    /// <param name="artifact">The complete policy artifact.</param>
    /// <returns>An exact policy copy with its computed content hash.</returns>
    public static HumanInputPolicyArtifact Apply(HumanInputPolicyArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return artifact with { ContentHash = Compute(artifact) };
    }

    /// <summary>Gets whether the stored policy hash exactly matches canonical content.</summary>
    /// <param name="artifact">The policy artifact to verify.</param>
    /// <returns><see langword="true"/> only when the canonical hash matches.</returns>
    public static bool Matches(HumanInputPolicyArtifact? artifact)
        => artifact is not null && IsSha256(artifact.ContentHash) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(artifact.ContentHash), Encoding.ASCII.GetBytes(Compute(artifact)));

    /// <summary>Gets whether a value has the exact lowercase SHA-256 hexadecimal shape.</summary>
    /// <param name="value">The untrusted hash text.</param>
    /// <returns><see langword="true"/> only for a lowercase 64-character hexadecimal hash.</returns>
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
