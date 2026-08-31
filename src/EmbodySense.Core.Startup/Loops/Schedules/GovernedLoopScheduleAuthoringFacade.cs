using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Loops.GraphAuthoring;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;
using EmbodySense.Core.Startup.Loops.Schedules.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Loops.Schedules;

/// <summary>Authors immutable schema-1 schedules from bounded visible-Web intent and current canonical evidence.</summary>
/// <remarks>
/// This facade is the only authoring bridge for schedules. It never accepts browser-selected authority, actor,
/// workspace, role, publication, grant, adapter, payload, schedule identity, time-zone fingerprint, UTC mapping, or
/// state version. Edits are immutable successor creations: callers use a new stable operation ID, then disable the
/// previous schedule through the existing operational-control facade with its exact current state revision.
/// </remarks>
public sealed class GovernedLoopScheduleAuthoringFacade
{
    private const string ScheduleIdDomain = "embodysense-governed-loop-schedule-id-v1";
    private const string TimeTriggerCapabilityId = "org.embodysense/triggers/time";
    private readonly AuthorityActorId _actor;
    private readonly IAuthorityGrantResolver _grants;
    private readonly GovernedLoopGraphAuthoringFacade _graphs;
    private readonly GovernedLoopInvocationPreparationFacade _invocations;
    private readonly ScheduleRuntimeFacade _schedules;
    private readonly string _surfaceId;
    private readonly SystemScheduleTimeZoneAdapter _timeZones;
    private readonly string _workspaceId;

    internal GovernedLoopScheduleAuthoringFacade(
        string workspaceId,
        string actorId,
        string surfaceId,
        GovernedLoopGraphAuthoringFacade graphs,
        GovernedLoopInvocationPreparationFacade invocations,
        IAuthorityGrantResolver grants,
        ScheduleRuntimeFacade schedules,
        SystemScheduleTimeZoneAdapter timeZones)
    {
        if (!AuthorityActorId.TryParse(actorId, out var actor, out _))
        {
            throw new ArgumentException("The configured schedule-authoring actor is invalid.", nameof(actorId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        _workspaceId = workspaceId;
        _surfaceId = surfaceId;
        _actor = actor!;
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _invocations = invocations ?? throw new ArgumentNullException(nameof(invocations));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _timeZones = timeZones ?? throw new ArgumentNullException(nameof(timeZones));
    }

    /// <summary>Rereads one exact canonical schedule definition and its current state without following successors.</summary>
    /// <param name="scheduleId">The stable schedule identity.</param>
    /// <param name="cancellationToken">Cancels the canonical reread.</param>
    /// <returns>A closed read result with a definition and state only when both are trustworthy.</returns>
    public async Task<GovernedLoopScheduleAuthoringResponse> ReadAsync(string? scheduleId, CancellationToken cancellationToken = default)
    {
        if (!ScheduleId.TryParse(scheduleId, out var parsedScheduleId))
        {
            return Response("invalid", string.Empty, "The schedule identifier is invalid.", null, null, null, null);
        }

        ScheduleStoreReadResult read;
        try
        {
            read = await _schedules.ReadAsync(parsedScheduleId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Response("unavailable", string.Empty, "The canonical schedule could not be read safely.", parsedScheduleId, null, null, null);
        }

        return read.Status switch
        {
            ScheduleStoreReadStatus.Found when read.Definition is not null && read.State is not null
                => Response("ready", string.Empty, "The canonical schedule is current.", parsedScheduleId, read.Definition, read.State, null),
            ScheduleStoreReadStatus.NotFound => Response("not-found", string.Empty, "The schedule does not exist.", parsedScheduleId, null, null, null),
            ScheduleStoreReadStatus.Backpressured => Response("backpressured", string.Empty, "The canonical schedule store is temporarily busy.", parsedScheduleId, null, null, null),
            ScheduleStoreReadStatus.Unavailable => Response("unavailable", string.Empty, "The canonical schedule store is unavailable.", parsedScheduleId, null, null, null),
            _ => Response("corrupt", string.Empty, "The canonical schedule evidence is malformed or contradictory.", parsedScheduleId, null, null, null),
        };
    }

    /// <summary>Validates, confirms when required, and creates or replays one immutable canonical schedule.</summary>
    /// <param name="input">Only bounded untrusted authoring intent and exact optimistic graph evidence.</param>
    /// <param name="cancellationToken">Cancels before a durable authority or schedule boundary.</param>
    /// <returns>The closed result and an authoritative reread after every durable outcome.</returns>
    public async Task<GovernedLoopScheduleAuthoringResponse> CreateAsync(
        GovernedLoopScheduleAuthoringInput? input,
        CancellationToken cancellationToken = default)
    {
        if (!IsInputShapeValid(input))
        {
            return Response("invalid", input?.OperationId ?? string.Empty, "The schedule authoring intent is malformed or outside schema-1 bounds.", null, null, null, null);
        }

        var scheduleId = DeriveScheduleId(input!.OperationId);
        if (scheduleId is null)
        {
            return Response("corrupt", input.OperationId, "The server could not derive a canonical schedule identity.", null, null, null, null);
        }

        var graph = await ReadExpectedGraphAsync(input, scheduleId, cancellationToken).ConfigureAwait(false);
        if (graph.Response is not null)
        {
            return graph.Response;
        }

        if (!_timeZones.TryCreateReference(input.TimeZoneId, out var timeZone))
        {
            return Response("invalid", input.OperationId, "The requested time-zone identifier is unavailable from the server-owned rules snapshot.", scheduleId, null, null, null);
        }

        var prepared = await _invocations.PrepareScheduleAsync(
            new GovernedLoopInvocationPreparationRequest(input.GraphId, input.RevisionId),
            cancellationToken).ConfigureAwait(false);
        var preparationFailure = PreparationFailure(prepared, input.OperationId, scheduleId);
        if (preparationFailure is not null)
        {
            return preparationFailure;
        }
        if (prepared.Publication is null)
        {
            return Response("unavailable", input.OperationId, "The current publication evidence is unavailable.", scheduleId, null, null, null);
        }

        var grant = await ResolveGrantAsync(input, prepared, cancellationToken).ConfigureAwait(false);
        if (grant.Response is not null)
        {
            return grant.Response;
        }

        var currentGraph = await ReadExpectedGraphAsync(input, scheduleId, cancellationToken).ConfigureAwait(false);
        if (currentGraph.Response is not null)
        {
            return currentGraph.Response;
        }

        var selectedArtifact = currentGraph.Graph!.Artifacts.SingleOrDefault(value => Equals(value.RevisionArtifact.Revision, prepared.Publication.Revision));
        if (!TryCreateDefinition(input, scheduleId, timeZone!, prepared.Publication, grant.Grant!, selectedArtifact, out var definition, out var definitionFailure))
        {
            return Response("invalid", input.OperationId, definitionFailure, scheduleId, null, null, null);
        }

        ScheduleRuntimeCreateResult created;
        try
        {
            created = await _schedules.CreateAsync(definition!, input.Enabled, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Response("unavailable", input.OperationId, "The canonical schedule could not be created safely.", scheduleId, null, null, null);
        }

        var reread = await ReadAsync(scheduleId.Value, cancellationToken).ConfigureAwait(false);
        return created.Status switch
        {
            ScheduleRuntimeCreateStatus.Created when reread.Status == "ready"
                => reread with { Status = "created", OperationId = input.OperationId, Detail = "The immutable canonical schedule was created." },
            ScheduleRuntimeCreateStatus.AlreadyExists when reread.Status == "ready"
                => reread with { Status = "replayed", OperationId = input.OperationId, Detail = "The exact immutable schedule creation was replayed." },
            ScheduleRuntimeCreateStatus.Conflict when reread.Status == "ready"
                => reread with { Status = "conflict", OperationId = input.OperationId, Detail = "This operation identity already owns a different immutable schedule definition." },
            ScheduleRuntimeCreateStatus.BoundExceeded => reread with { Status = "invalid", OperationId = input.OperationId, Detail = "The bounded initial recurrence scan could not reach a valid occurrence." },
            ScheduleRuntimeCreateStatus.Backpressured => reread with { Status = "backpressured", OperationId = input.OperationId, Detail = "The canonical schedule store is temporarily busy." },
            ScheduleRuntimeCreateStatus.Unavailable => reread with { Status = "unavailable", OperationId = input.OperationId, Detail = "The canonical schedule dependencies are unavailable." },
            _ => reread with { Status = "corrupt", OperationId = input.OperationId, Detail = "The schedule create result was malformed or contradictory." },
        };
    }

    private async Task<ExpectedGraphResult> ReadExpectedGraphAsync(
        GovernedLoopScheduleAuthoringInput input,
        ScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        GovernedLoopGraphReadResponse graph;
        try
        {
            graph = await _graphs.ReadAsync(input.GraphId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ExpectedGraphResult(Response("unavailable", input.OperationId, "The selected graph could not be read safely.", scheduleId, null, null, null), null);
        }

        if (graph.Status == "not-found")
        {
            return new ExpectedGraphResult(Response("not-found", input.OperationId, "The selected graph does not exist.", scheduleId, null, null, null), null);
        }

        if (graph.Status != "ready" || graph.Lifecycle is null)
        {
            return new ExpectedGraphResult(Response("unavailable", input.OperationId, "The selected graph lifecycle is unavailable.", scheduleId, null, null, null), null);
        }

        if (graph.Lifecycle.LifecycleVersion != input.ExpectedGraphLifecycleVersion)
        {
            return new ExpectedGraphResult(Response("stale", input.OperationId, "The selected graph lifecycle version is no longer current.", scheduleId, null, null, null), null);
        }

        return new ExpectedGraphResult(null, graph);
    }

    private async Task<ResolvedGrantResult> ResolveGrantAsync(
        GovernedLoopScheduleAuthoringInput input,
        GovernedLoopInvocationPreparationResponse prepared,
        CancellationToken cancellationToken)
    {
        AuthorityGrantReference? selected = null;
        if (prepared.Status == GovernedLoopInvocationPreparationStatus.Ready)
        {
            if (prepared.EligibleGrants.Count != 1)
            {
                return new ResolvedGrantResult(Response("conflict", input.OperationId, "The current authority grant choices are ambiguous; reread before authoring a schedule.", null, null, null, null), null);
            }

            selected = prepared.EligibleGrants[0].Grant;
        }
        else
        {
            if (prepared.Preview is null || string.IsNullOrEmpty(input.ExpectedAuthorityPreviewHash))
            {
                return new ResolvedGrantResult(Response("confirmation-required", input.OperationId, prepared.Detail, null, null, null, prepared.Preview), null);
            }

            var confirmed = await _invocations.ConfirmScheduleAsync(
                new GovernedLoopInvocationAuthorityConfirmation(
                    input.GraphId,
                    input.RevisionId,
                    input.ExpectedAuthorityPreviewHash,
                    input.OperationId),
                cancellationToken).ConfigureAwait(false);
            if (confirmed.Status != GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed || confirmed.Grant is null)
            {
                return new ResolvedGrantResult(Response(MapConfirmationStatus(confirmed.Status), input.OperationId, confirmed.Detail, null, null, null, null), null);
            }

            selected = confirmed.Grant;
        }

        AuthorityGrantResolution resolved;
        try
        {
            resolved = await _grants.ResolveAsync(selected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ResolvedGrantResult(Response("unavailable", input.OperationId, "The selected exact authority grant could not be resolved safely.", null, null, null, null), null);
        }

        return resolved.Status == AuthorityGrantResolutionStatus.Active
            && resolved.Grant is not null
            && Equals(resolved.RequestedReference, selected)
            ? new ResolvedGrantResult(null, resolved.Grant)
            : new ResolvedGrantResult(Response(MapGrantStatus(resolved.Status), input.OperationId, "The selected exact authority grant is no longer active.", null, null, null, null), null);
    }

    private bool TryCreateDefinition(
        GovernedLoopScheduleAuthoringInput input,
        ScheduleId scheduleId,
        ScheduleTimeZoneReference timeZone,
        GovernedLoopRevisionPublicationPin publication,
        AuthorityGrant grant,
        GovernedLoopGraphRevisionArtifact? artifact,
        out ScheduleDefinition? definition,
        out string failure)
    {
        definition = null;
        failure = "The selected current graph, grant, publication, or time adapter cannot form a canonical schema-1 schedule.";
        if (!Equals(grant.Binding.Loop, publication)
            || artifact is null
            || !Equals(artifact.RevisionArtifact.Revision, publication.Revision)
            || !Equals(artifact.Graph.OwningRole, grant.Binding.Role)
            || !TryCreateTimeAdapter(out var timeAdapter)
            || !TriggerDeliveryFactory.TryCreateGovernedLoopReference(
                publication,
                new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
                out var target,
                out _))
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = new UTF8Encoding(false, true).GetBytes(artifact.Graph.Purpose);
        }
        catch (ArgumentException)
        {
            failure = "The immutable published graph purpose cannot form bounded UTF-8 schedule payload evidence.";
            return false;
        }

        if (payload.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes)
        {
            failure = "The immutable published graph purpose exceeds the bounded schedule payload limit.";
            return false;
        }

        definition = new ScheduleDefinition(
            ScheduleDefinition.CurrentSchemaVersion,
            scheduleId,
            1,
            target!,
            timeAdapter!,
            _actor,
            _surfaceId,
            _workspaceId,
            grant.Binding.Role.Identity.RoleId,
            grant.Binding.Profile.Reference,
            new SchedulePayloadReference(GovernedLoopSchedulePayloadSource.CreateReference(scheduleId), CapabilityIntegrityDigest.Compute(payload)),
            input.Priority,
            new ScheduleRecurrenceRule(input.RecurrenceKind, input.FirstLocalOccurrence, input.FixedIntervalSeconds),
            timeZone,
            new ScheduleDaylightSavingPolicy(input.InvalidLocalTime, input.AmbiguousLocalTime),
            new ScheduleMisfirePolicy(input.MisfireKind, input.CatchUpLimit),
            input.Overlap,
            true);
        var validation = ScheduleContractValidator.ValidateDefinition(definition);
        if (validation.IsValid)
        {
            return true;
        }

        definition = null;
        failure = $"The requested schedule policies violate canonical schema-1 validation ({validation.Errors[0].Code}).";
        return false;
    }

    private static GovernedLoopScheduleAuthoringResponse? PreparationFailure(
        GovernedLoopInvocationPreparationResponse prepared,
        string operationId,
        ScheduleId scheduleId)
        => prepared.Status switch
        {
            GovernedLoopInvocationPreparationStatus.Ready or GovernedLoopInvocationPreparationStatus.ConfirmationRequired => null,
            GovernedLoopInvocationPreparationStatus.Invalid => Response("invalid", operationId, prepared.Detail, scheduleId, null, null, null),
            GovernedLoopInvocationPreparationStatus.NotFound => Response("not-found", operationId, prepared.Detail, scheduleId, null, null, null),
            GovernedLoopInvocationPreparationStatus.Stale => Response("stale", operationId, prepared.Detail, scheduleId, null, null, null),
            GovernedLoopInvocationPreparationStatus.Ineligible => Response("ineligible", operationId, prepared.Detail, scheduleId, null, null, null),
            _ => Response("unavailable", operationId, prepared.Detail, scheduleId, null, null, null),
        };

    private static bool IsInputShapeValid(GovernedLoopScheduleAuthoringInput? input)
        => input is not null
            && CustomLoopArtifactIdentifier.IsValid(input.OperationId, 120)
            && CustomLoopArtifactIdentifier.IsValid(input.GraphId)
            && CustomLoopArtifactIdentifier.IsValid(input.RevisionId)
            && input.ExpectedGraphLifecycleVersion > 0
            && input.FirstLocalOccurrence.Kind == DateTimeKind.Unspecified
            && input.TimeZoneId is { Length: > 0 and <= ScheduleContractLimits.MaxTimeZoneIdCharacters }
            && Enum.IsDefined(input.RecurrenceKind)
            && Enum.IsDefined(input.InvalidLocalTime)
            && Enum.IsDefined(input.AmbiguousLocalTime)
            && Enum.IsDefined(input.MisfireKind)
            && Enum.IsDefined(input.Overlap)
            && Enum.IsDefined(input.Priority);

    private static ScheduleId? DeriveScheduleId(string operationId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ScheduleIdDomain + "\n" + operationId)));
        return ScheduleId.TryParse("schedule-" + hash[..48], out var scheduleId) ? scheduleId : null;
    }

    private static bool TryCreateTimeAdapter(out TriggerAdapterReference? adapter)
    {
        adapter = null;
        var descriptor = BuiltInCapabilityCatalog.Descriptors.SingleOrDefault(value => value.Id.Value == TimeTriggerCapabilityId);
        return descriptor is not null
            && CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _)
            && (adapter = new TriggerAdapterReference(identity!, descriptor.Implementation)) is not null;
    }

    private static string MapConfirmationStatus(GovernedLoopInvocationAuthorityConfirmationStatus status)
        => status switch
        {
            GovernedLoopInvocationAuthorityConfirmationStatus.Invalid => "invalid",
            GovernedLoopInvocationAuthorityConfirmationStatus.Stale => "stale",
            GovernedLoopInvocationAuthorityConfirmationStatus.Ineligible => "ineligible",
            GovernedLoopInvocationAuthorityConfirmationStatus.Conflict => "conflict",
            GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable => "unavailable",
            _ => "corrupt",
        };

    private static string MapGrantStatus(AuthorityGrantResolutionStatus status)
        => status switch
        {
            AuthorityGrantResolutionStatus.Invalid => "invalid",
            AuthorityGrantResolutionStatus.NotFound => "not-found",
            AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired => "stale",
            AuthorityGrantResolutionStatus.Unavailable or AuthorityGrantResolutionStatus.Ambiguous => "unavailable",
            _ => "ineligible",
        };

    private static GovernedLoopScheduleAuthoringResponse Response(
        string status,
        string operationId,
        string detail,
        ScheduleId? scheduleId,
        ScheduleDefinition? definition,
        ScheduleState? state,
        GovernedLoopInvocationAuthorityPreview? preview)
    {
        _ = scheduleId;
        return new(status, operationId, detail, Project(definition, state), preview?.SemanticHash);
    }

    private static GovernedLoopScheduleAuthoringSnapshot? Project(ScheduleDefinition? definition, ScheduleState? state)
    {
        if (definition?.Target.GovernedPublication is null || state is null)
        {
            return null;
        }

        return new GovernedLoopScheduleAuthoringSnapshot(
            definition.ScheduleId.Value,
            definition.Target.GovernedPublication.Revision.GraphId,
            definition.Target.GovernedPublication.Revision.RevisionId,
            state.Enabled,
            state.StateRevision,
            state.NextOccurrence?.ScheduledAtUtc,
            Token(definition.Recurrence.Kind),
            definition.Recurrence.FirstLocalOccurrence,
            definition.Recurrence.FixedIntervalSeconds,
            definition.TimeZone.TimeZoneId,
            Token(definition.DaylightSaving.InvalidLocalTime),
            Token(definition.DaylightSaving.AmbiguousLocalTime),
            Token(definition.Misfire.Kind),
            definition.Misfire.CatchUpLimit,
            Token(definition.Overlap),
            Token(definition.Priority));
    }

    private static string Token<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var text = value.ToString();
        var builder = new StringBuilder(text.Length + 4);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (index > 0 && char.IsUpper(current))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private sealed record ExpectedGraphResult(GovernedLoopScheduleAuthoringResponse? Response, GovernedLoopGraphReadResponse? Graph);

    private sealed record ResolvedGrantResult(GovernedLoopScheduleAuthoringResponse? Response, AuthorityGrant? Grant);
}
