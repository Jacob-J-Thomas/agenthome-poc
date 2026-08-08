using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Computes the deterministic lowercase SHA-256 identity of a canonical trigger-delivery envelope.
/// </summary>
public static class TriggerDeliveryHash
{
    /// <summary>
    /// Computes the canonical envelope hash after complete validation.
    /// </summary>
    /// <param name="envelope">The envelope to validate and hash.</param>
    /// <param name="hash">The lowercase SHA-256 hash when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when hashing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryCompute(TriggerDeliveryEnvelope? envelope, out string? hash, out TriggerContractValidationResult validation)
    {
        if (!TriggerDeliveryJson.TrySerialize(envelope, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json!))).ToLowerInvariant();
        return true;
    }
}
