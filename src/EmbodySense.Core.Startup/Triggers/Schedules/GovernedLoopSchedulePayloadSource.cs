using System.Text;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Resolves a schedule's canonical published graph purpose as non-persisted governed payload evidence.</summary>
/// <remarks>
/// The opaque <c>payload/{schedule-id}</c> identity is never interpreted as a file, URL, or user-controlled locator.
/// Resolution first rereads the canonical schedule definition, proves that it owns the exact identity, then rereads the
/// exact graph revision pinned by that definition. The payload is the already-published bounded graph purpose; no Web
/// request content, secrets, parallel schedule document, or ambient current revision is retained or followed.
/// </remarks>
public sealed class GovernedLoopSchedulePayloadSource : IScheduleGovernedPayloadSource
{
    private const string PayloadPrefix = "payload/";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private readonly IGovernedLoopGraphRevisionStore _graphs;
    private readonly IScheduleStorePort _schedules;

    /// <summary>Creates the source over canonical schedule and immutable graph-revision stores.</summary>
    /// <param name="schedules">The canonical schedule definition and state store.</param>
    /// <param name="graphs">The canonical immutable graph-revision store.</param>
    /// <exception cref="ArgumentNullException">A required canonical store is absent.</exception>
    public GovernedLoopSchedulePayloadSource(IScheduleStorePort schedules, IGovernedLoopGraphRevisionStore graphs)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    }

    /// <inheritdoc />
    public async Task<ScheduleGovernedPayloadResolution> ResolveAsync(
        string governedReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseScheduleId(governedReference, out var scheduleId))
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.NotFound, null, null, null);
        }

        ScheduleStoreReadResult schedule;
        try
        {
            schedule = await _schedules.ReadAsync(scheduleId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.Unavailable, null, null, null);
        }

        if (schedule.Status != ScheduleStoreReadStatus.Found || schedule.Definition is null)
        {
            return Resolution(MapScheduleStatus(schedule.Status), null, null, null);
        }

        if (!string.Equals(schedule.Definition.Payload.GovernedReference, governedReference, StringComparison.Ordinal)
            || schedule.Definition.Target.GovernedPublication is null)
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.Corrupt, null, null, null);
        }

        GovernedLoopGraphRevisionArtifactReadResult graph;
        try
        {
            graph = await _graphs.ReadArtifactAsync(schedule.Definition.Target.GovernedPublication.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.Unavailable, null, null, null);
        }

        if (graph.Status != GovernedLoopRevisionStoreReadStatus.Ready || graph.Artifact is null)
        {
            return Resolution(MapGraphStatus(graph.Status), null, null, null);
        }

        if (!Equals(graph.Artifact.RevisionArtifact.Revision, schedule.Definition.Target.GovernedPublication.Revision))
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.Corrupt, null, null, null);
        }

        try
        {
            var content = _strictUtf8.GetBytes(graph.Artifact.Graph.Purpose);
            if (content.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes)
            {
                return Resolution(ScheduleGovernedPayloadResolutionStatus.Corrupt, null, null, null);
            }

            return Resolution(
                ScheduleGovernedPayloadResolutionStatus.Available,
                governedReference,
                CapabilityIntegrityDigest.Compute(content),
                content);
        }
        catch (ArgumentException)
        {
            return Resolution(ScheduleGovernedPayloadResolutionStatus.Corrupt, null, null, null);
        }
    }

    /// <summary>Derives the sole opaque payload identity admitted for one canonical schedule identifier.</summary>
    /// <param name="scheduleId">The exact canonical schedule identity.</param>
    /// <returns>The non-locator payload identity.</returns>
    public static string CreateReference(ScheduleId scheduleId)
    {
        ArgumentNullException.ThrowIfNull(scheduleId);
        return PayloadPrefix + scheduleId.Value;
    }

    private static ScheduleGovernedPayloadResolution Resolution(
        ScheduleGovernedPayloadResolutionStatus status,
        string? governedReference,
        CapabilityIntegrityDigest? digest,
        byte[]? content)
        => new(status, governedReference, digest, content);

    private static ScheduleGovernedPayloadResolutionStatus MapScheduleStatus(ScheduleStoreReadStatus status)
        => status switch
        {
            ScheduleStoreReadStatus.NotFound => ScheduleGovernedPayloadResolutionStatus.NotFound,
            ScheduleStoreReadStatus.Backpressured => ScheduleGovernedPayloadResolutionStatus.Backpressured,
            ScheduleStoreReadStatus.Unavailable => ScheduleGovernedPayloadResolutionStatus.Unavailable,
            _ => ScheduleGovernedPayloadResolutionStatus.Corrupt,
        };

    private static ScheduleGovernedPayloadResolutionStatus MapGraphStatus(GovernedLoopRevisionStoreReadStatus status)
        => status switch
        {
            GovernedLoopRevisionStoreReadStatus.NotFound => ScheduleGovernedPayloadResolutionStatus.NotFound,
            GovernedLoopRevisionStoreReadStatus.Unavailable => ScheduleGovernedPayloadResolutionStatus.Unavailable,
            _ => ScheduleGovernedPayloadResolutionStatus.Corrupt,
        };

    private static bool TryParseScheduleId(string? governedReference, out ScheduleId? scheduleId)
    {
        scheduleId = null;
        return governedReference is not null
            && governedReference.Length > PayloadPrefix.Length
            && governedReference.StartsWith(PayloadPrefix, StringComparison.Ordinal)
            && ScheduleId.TryParse(governedReference[PayloadPrefix.Length..], out scheduleId);
    }
}
