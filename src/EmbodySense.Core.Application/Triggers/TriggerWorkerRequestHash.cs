using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Computes the exact envelope, ownership, and authority proof binding used for durable dispatch.</summary>
public static class TriggerWorkerRequestHash
{
    /// <summary>Computes the filename-safe governed invocation identity for one delivery lease generation.</summary>
    /// <param name="deliveryId">The exact trigger delivery identity.</param>
    /// <param name="leaseGeneration">The exact positive ownership generation.</param>
    /// <returns>A deterministic operation identity within the governed loop runtime bound.</returns>
    public static string ComputeOperationId(TriggerDeliveryId deliveryId, long leaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        if (leaseGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration));
        }

        var canonical = string.Join('\n', deliveryId.Value, leaseGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "trigger-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Computes a lowercase SHA-256 request binding without granting dispatch authority.</summary>
    /// <param name="envelope">The selected canonical envelope.</param>
    /// <param name="lease">The exact live ownership evidence.</param>
    /// <param name="authorityEvidenceHash">The exact current-evidence proof hash.</param>
    /// <returns>The lowercase SHA-256 request hash.</returns>
    public static string Compute(TriggerDeliveryEnvelope envelope, TriggerWorkerLease lease, string authorityEvidenceHash)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityEvidenceHash);
        if (!TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _))
        {
            throw new InvalidOperationException("A selected trigger envelope could not be hashed.");
        }

        var canonical = string.Join('\n', envelopeHash, lease.WorkerId, lease.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), authorityEvidenceHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
