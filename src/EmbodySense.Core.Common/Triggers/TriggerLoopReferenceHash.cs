using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>Computes a domain-separated deterministic identity for one exact trigger loop reference.</summary>
public static class TriggerLoopReferenceHash
{
    private const string Domain = "embodysense-trigger-loop-reference-v1\0";

    /// <summary>Computes the lowercase SHA-256 hash of the validated canonical loop-reference JSON.</summary>
    /// <param name="loop">The exact legacy or governed loop reference.</param>
    /// <param name="hash">The 64-character lowercase hexadecimal digest when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the reference is valid and hashable; otherwise, <see langword="false"/>.</returns>
    public static bool TryCompute(TriggerLoopReference? loop, out string? hash, out TriggerContractValidationResult validation)
    {
        validation = TriggerDeliveryValidator.ValidateLoopReference(loop);
        if (!validation.IsValid)
        {
            hash = null;
            return false;
        }

        if (!TriggerDeliveryJson.TrySerializeLoopReferenceKnownValid(loop!, out var json, out var error))
        {
            hash = null;
            validation = new TriggerContractValidationResult([error!]);
            return false;
        }

        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Domain + json))).ToLowerInvariant();
        return true;
    }
}
