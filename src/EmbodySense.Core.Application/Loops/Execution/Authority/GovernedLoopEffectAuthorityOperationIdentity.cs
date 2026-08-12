using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Derives server-owned deterministic effect-operation identities from exact immutable run proof.</summary>
public static class GovernedLoopEffectAuthorityOperationIdentity
{
    private const string ConversationPublicationDomain = "embodysense-conversation-publication-effect-v1";
    private const string ConversationPublicationTargetDomain = "embodysense-conversation-publication-target-v1";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Creates the canonical non-secret target fingerprint for the invoking conversation captured by immutable admission intent.</summary>
    /// <param name="receipt">The exact successful admission receipt whose intent captures the invoking target.</param>
    /// <returns>A domain-separated lowercase SHA-256 target fingerprint.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the exact retained receipt is missing.</exception>
    /// <exception cref="ArgumentException">Thrown when the retained admission proof is malformed.</exception>
    public static string CreateConversationPublicationTargetFingerprint(
        GovernedLoopAdmissionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!GovernedLoopAdmissionValidator.Validate(receipt).IsValid)
        {
            throw new ArgumentException("The conversation-publication target requires one exact successful admission receipt.", nameof(receipt));
        }

        var canonical = new StringBuilder(256);
        Append(canonical, ConversationPublicationTargetDomain);
        Append(canonical, GovernedLoopAdmissionContractHash.ComputeIntentHash(receipt.Intent));
        return Digest(canonical);
    }

    /// <summary>Creates the exact operation identity for one canonical conversation publication.</summary>
    /// <param name="receipt">The exact successful admission receipt.</param>
    /// <param name="binding">The exact run, revision, and execution generation.</param>
    /// <param name="artifact">The exact immutable graph artifact.</param>
    /// <param name="nodeId">The exact success-Exit node identity.</param>
    /// <param name="nodeAttempt">The exact positive node attempt.</param>
    /// <param name="publicationOperationId">The stable conversation-publication correlation identity.</param>
    /// <param name="targetFingerprint">The exact canonical invoking-conversation target fingerprint.</param>
    /// <returns>A deterministic bounded effect-operation identifier.</returns>
    /// <exception cref="ArgumentNullException">Thrown when exact retained evidence is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the node attempt is unsupported.</exception>
    /// <exception cref="ArgumentException">Thrown when a node or publication identity is invalid.</exception>
    public static string CreateConversationPublication(
        GovernedLoopAdmissionReceipt receipt,
        GovernedLoopExecutionBinding binding,
        GovernedLoopGraphRevisionArtifact artifact,
        string nodeId,
        int nodeAttempt,
        string publicationOperationId,
        string targetFingerprint)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(artifact);
        if (nodeAttempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeAttempt), nodeAttempt, "The node attempt is outside the governed effect-authority bound.");
        }

        nodeId = CustomLoopArtifactIdentifier.Require(nodeId, nameof(nodeId), GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        publicationOperationId = CustomLoopArtifactIdentifier.Require(
            publicationOperationId,
            nameof(publicationOperationId),
            GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        if (!IsLowerSha256(targetFingerprint))
        {
            throw new ArgumentException("The conversation-publication target fingerprint must be one canonical lowercase SHA-256 digest.", nameof(targetFingerprint));
        }

        var canonical = new StringBuilder(1_024);
        Append(canonical, ConversationPublicationDomain);
        Append(canonical, receipt.ContentHash);
        Append(canonical, binding.RunId);
        Append(canonical, binding.ExecutionGeneration.ToString(CultureInfo.InvariantCulture));
        Append(canonical, binding.Revision.GraphId);
        Append(canonical, binding.Revision.RevisionId);
        Append(canonical, binding.Revision.ExecutableHash);
        Append(canonical, artifact.ArtifactHash);
        Append(canonical, artifact.LayoutHash);
        Append(canonical, nodeId);
        Append(canonical, nodeAttempt.ToString(CultureInfo.InvariantCulture));
        Append(canonical, publicationOperationId);
        Append(canonical, targetFingerprint);
        return "conversation-publication-" + Digest(canonical);
    }

    private static string Digest(StringBuilder canonical)
        => Convert.ToHexString(SHA256.HashData(_strictUtf8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Append(StringBuilder canonical, string value)
    {
        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
        canonical.Append('|');
    }
}
