using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Common.Loops.Posture;

/// <summary>Computes domain-separated canonical hashes for operational authority, requests, and receipts.</summary>
public static class GovernedLoopOperationalHash
{
    /// <summary>Computes current local-control authority evidence.</summary>
    public static string Authority(string workspaceId, string actorId, string surfaceId, DateTimeOffset observedAtUtc, bool permitted, string reasonCode)
    {
        if (!GovernedLoopOperationalContract.IsUtc(observedAtUtc))
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc));
        }
        return Hash("embodysense.governed-loop.operational-authority.v1", workspaceId, actorId, surfaceId, permitted ? "1" : "0", reasonCode);
    }

    /// <summary>Computes the exact public control-request binding.</summary>
    public static string Request(GovernedLoopOperationalControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Hash(
            "embodysense.governed-loop.operational-control-request.v1",
            request.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.WorkspaceId,
            request.OperationId,
            ((int)request.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.TargetId,
            request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ExpectedEvidenceHash,
            request.ExpectedAuthorityEvidenceHash,
            request.ActorId,
            request.SurfaceId,
            request.MaximumBatchItems.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Computes one receipt's content hash while excluding its hash field.</summary>
    public static string Receipt(GovernedLoopOperationalControlReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var parts = new List<string>
        {
            "embodysense.governed-loop.operational-control-receipt.v1",
            receipt.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.WorkspaceId,
            receipt.OperationId,
            receipt.RequestHash,
            ((int)receipt.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.TargetId,
            receipt.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.ExpectedEvidenceHash,
            receipt.ActorId,
            receipt.SurfaceId,
            receipt.AuthorityEvidenceHash,
            receipt.PreviousContentHash ?? string.Empty,
            Timestamp(receipt.RequestedAtUtc),
            Timestamp(receipt.UpdatedAtUtc),
            ((int)receipt.State).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)receipt.Outcome).ToString(System.Globalization.CultureInfo.InvariantCulture),
            receipt.ReasonCode,
            receipt.Progress.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (var item in receipt.Progress)
        {
            parts.Add(item.TargetId);
            parts.Add(item.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(item.ExpectedEvidenceHash);
            parts.Add(((int)item.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(item.CurrentRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            parts.Add(item.CurrentEvidenceHash ?? string.Empty);
            parts.Add(item.ReasonCode);
        }
        return Hash(parts.ToArray());
    }

    /// <summary>Combines already-canonical evidence hashes without treating the result as new authority.</summary>
    public static string Evidence(params string[] hashes) => Hash(["embodysense.governed-loop.operational-evidence.v1", .. hashes]);

    /// <summary>Computes exact queue catalog evidence without payload values.</summary>
    public static string QueueCatalog(long generation, int queuedEntries, long queuedReservationBytes, int retainedEntries, long retainedReservationBytes, bool backpressured)
        => Hash(
            "embodysense.governed-loop.queue-catalog.v1",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            queuedEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            queuedReservationBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            retainedEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            retainedReservationBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            backpressured ? "1" : "0");

    private static string Hash(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part ?? throw new ArgumentNullException(nameof(parts)));
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
