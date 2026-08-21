using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

internal sealed class StubOperationalTriggerQueuePort : ITriggerQueueQueryPort, ITriggerQueueCancellationPort, IGovernedLoopQueueOperationalPosturePort
{
    internal TriggerQueueSnapshot Snapshot { get; set; } = null!;
    internal Func<TriggerDeliveryId, long, DateTimeOffset, TriggerQueueCancellationResult>? Cancellation { get; set; }
    internal List<(TriggerDeliveryId DeliveryId, long Revision, DateTimeOffset CancelledAtUtc)> Cancellations { get; } = [];

    public Task<TriggerQueueSnapshot> GetSnapshotAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot);

    public Task<GovernedLoopQueueEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var start = 0;
        if (request.AfterId is not null && !TryParseCursor(request.AfterId, out start))
        {
            return Task.FromResult(Corrupt());
        }
        var candidates = Snapshot.Entries.Skip(start).ToArray();
        var items = candidates.Take(request.MaximumCount).ToArray();
        var hasMore = candidates.Length > items.Length;
        return Task.FromResult(new GovernedLoopQueueEvidenceReadResult(
            items.Length == 0 ? GovernedLoopOperationalEvidenceReadStatus.Empty : GovernedLoopOperationalEvidenceReadStatus.Found,
            Snapshot.Generation,
            Snapshot.QueuedEntries,
            Snapshot.QueuedReservationBytes,
            Snapshot.RetainedEntries,
            Snapshot.RetainedReservationBytes,
            Snapshot.PersistenceBackpressured,
            hasMore,
            hasMore ? Cursor(checked(start + items.Length), items[^1].DeliveryId.Value) : null,
            Array.AsReadOnly(items)));
    }

    private bool TryParseCursor(string value, out int nextIndex)
    {
        nextIndex = 0;
        var parts = value.Split('.');
        if (parts.Length != 4
            || parts[0] != "q1"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation != Snapshot.Generation
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out nextIndex)
            || nextIndex < 1
            || nextIndex >= Snapshot.Entries.Count)
        {
            return false;
        }
        var encodedIdentity = Convert.ToBase64String(Encoding.UTF8.GetBytes(Snapshot.Entries[nextIndex - 1].DeliveryId.Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.Equals(parts[3], encodedIdentity, StringComparison.Ordinal)
            && string.Equals(value, Cursor(nextIndex, Snapshot.Entries[nextIndex - 1].DeliveryId.Value), StringComparison.Ordinal);
    }

    private string Cursor(int nextIndex, string deliveryId)
    {
        var identity = Convert.ToBase64String(Encoding.UTF8.GetBytes(deliveryId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.Join('.', "q1", Snapshot.Generation.ToString(CultureInfo.InvariantCulture), nextIndex.ToString(CultureInfo.InvariantCulture), identity);
    }

    private static GovernedLoopQueueEvidenceReadResult Corrupt()
        => new(GovernedLoopOperationalEvidenceReadStatus.Corrupt, 0, 0, 0, 0, 0, false, false, null, []);

    public Task<TriggerQueueCancellationResult> CancelAsync(TriggerDeliveryId deliveryId, long expectedRevision, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default)
    {
        Cancellations.Add((deliveryId, expectedRevision, cancelledAtUtc));
        return Task.FromResult(Cancellation?.Invoke(deliveryId, expectedRevision, cancelledAtUtc)
            ?? new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.NotFound, null));
    }
}
