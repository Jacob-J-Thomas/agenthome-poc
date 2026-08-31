using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Computes the value-free receipt binding one Human Review release operation to its exact evidence and frontier.</summary>
/// <remarks>The receipt retains only immutable operation and evidence hashes, never reviewer content or executable payloads.</remarks>
public static class GovernedLoopHumanReviewReleaseReceiptHash
{
    /// <summary>Computes the canonical release result hash.</summary>
    /// <param name="operationId">The stable release or non-approval action identity.</param>
    /// <param name="outcomeEvidenceHash">The exact terminal node evidence hash, or unchanged parked-frontier hash for information acknowledgement.</param>
    /// <param name="frontierReceiptHash">The exact resulting canonical frontier receipt hash.</param>
    /// <returns>The lower-case SHA-256 result binding the operation, evidence, and frontier.</returns>
    public static string Compute(string operationId, string outcomeEvidenceHash, string frontierReceiptHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeEvidenceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(frontierReceiptHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "governed-loop-human-review-release-v1", operationId, outcomeEvidenceHash, frontierReceiptHash))));
    }

    /// <summary>Determines whether a retained result hash exactly binds the supplied immutable release coordinates.</summary>
    /// <param name="resultHash">The retained result hash.</param>
    /// <param name="operationId">The stable release or non-approval action identity.</param>
    /// <param name="outcomeEvidenceHash">The exact terminal node evidence hash, or unchanged parked-frontier hash for information acknowledgement.</param>
    /// <param name="frontierReceiptHash">The exact resulting canonical frontier receipt hash.</param>
    /// <returns><see langword="true"/> only for an exact canonical receipt match.</returns>
    public static bool Matches(string? resultHash, string operationId, string outcomeEvidenceHash, string frontierReceiptHash)
    {
        try
        {
            return string.Equals(resultHash, Compute(operationId, outcomeEvidenceHash, frontierReceiptHash), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
