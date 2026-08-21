using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Common.Loops.Posture;

/// <summary>Validates the closed schema-1 operational posture and control boundary.</summary>
public static class GovernedLoopOperationalContract
{
    /// <summary>Determines whether a public posture query is canonical and bounded.</summary>
    public static bool IsValid(GovernedLoopOperationalPostureQuery? query)
        => query is not null
            && IsPageBound(query.MaximumQueueEntries)
            && IsPageBound(query.MaximumSchedules)
            && IsPageBound(query.MaximumWakes)
            && IsPageBound(query.MaximumRuns)
            && (query.QueueCursor is null || IsQueueCursor(query.QueueCursor))
            && IsOptionalIdentifier(query.AfterScheduleId)
            && IsOptionalIdentifier(query.AfterCheckpointId)
            && IsOptionalRunCursor(query.AfterRunId);

    /// <summary>Determines whether a public operational-control request is canonical and bounded.</summary>
    public static bool IsValid(GovernedLoopOperationalControlRequest? request)
        => request is not null
            && request.SchemaVersion == GovernedLoopOperationalControlRequest.CurrentSchemaVersion
            && Enum.IsDefined(request.Kind)
            && IsWorkspaceId(request.WorkspaceId)
            && IsIdentifier(request.OperationId, GovernedLoopOperationalPostureLimits.MaxOperationIdCharacters)
            && IsControlTarget(request.Kind, request.TargetId)
            && IsIdentifier(request.ActorId, GovernedLoopOperationalPostureLimits.MaxActorIdCharacters)
            && IsIdentifier(request.SurfaceId, GovernedLoopOperationalPostureLimits.MaxSurfaceIdCharacters)
            && request.ExpectedRevision > 0
            && IsHash(request.ExpectedEvidenceHash)
            && IsHash(request.ExpectedAuthorityEvidenceHash)
            && request.MaximumBatchItems is > 0 and <= GovernedLoopOperationalPostureLimits.MaxControlBatchItems
            && (request.Kind == GovernedLoopOperationalControlKind.CancelPendingDeliveries || request.MaximumBatchItems == 1);

    /// <summary>Determines whether trusted authority evidence is canonical.</summary>
    public static bool IsValid(GovernedLoopOperationalControlAuthority? authority)
        => authority is not null
            && authority.SchemaVersion == GovernedLoopOperationalControlAuthority.CurrentSchemaVersion
            && IsWorkspaceId(authority.WorkspaceId)
            && IsIdentifier(authority.ActorId, GovernedLoopOperationalPostureLimits.MaxActorIdCharacters)
            && IsIdentifier(authority.SurfaceId, GovernedLoopOperationalPostureLimits.MaxSurfaceIdCharacters)
            && IsUtc(authority.ObservedAtUtc)
            && IsHash(authority.EvidenceHash)
            && IsReason(authority.ReasonCode);

    /// <summary>Determines whether a lowercase SHA-256 value is canonical.</summary>
    public static bool IsHash(string? value)
        => value is { Length: GovernedLoopOperationalPostureLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Determines whether a workspace identity uses the shared canonical workspace-scope form.</summary>
    public static bool IsWorkspaceId(string? value) => ContextualRoleWorkspaceId.IsValid(value);

    /// <summary>Determines whether an opaque run-page continuation cursor is finite and transport safe.</summary>
    public static bool IsRunCursor(string? value)
        => value is { Length: > 0 and <= 1_024 }
            && value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));

    /// <summary>Determines whether an opaque queue-page cursor has a bounded transport-safe shape.</summary>
    public static bool IsQueueCursor(string? value)
        => value is { Length: > 0 and <= 1_024 }
            && value.StartsWith("q1.", StringComparison.Ordinal)
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>Determines whether an optional source identity cursor is canonical.</summary>
    public static bool IsOptionalArtifactCursor(string? value)
        => value is null || IsIdentifier(value, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters);

    /// <summary>Determines whether an optional run continuation cursor is canonical.</summary>
    public static bool IsOptionalRunCursor(string? value) => value is null || IsRunCursor(value);

    /// <summary>Determines whether an instant is nondefault UTC.</summary>
    public static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsPageBound(int value) => value is > 0 and <= GovernedLoopOperationalPostureLimits.MaxPageItems;

    private static bool IsControlTarget(GovernedLoopOperationalControlKind kind, string? value)
        => kind switch
        {
            GovernedLoopOperationalControlKind.PauseRun
                or GovernedLoopOperationalControlKind.CancelRun
                or GovernedLoopOperationalControlKind.ResumeRun
                or GovernedLoopOperationalControlKind.CancelPendingDeliveries => CustomLoopArtifactIdentifier.IsValid(value),
            GovernedLoopOperationalControlKind.DisableSchedule
                or GovernedLoopOperationalControlKind.EnableSchedule => ScheduleId.TryParse(value, out _),
            GovernedLoopOperationalControlKind.CancelDelivery => TriggerDeliveryId.TryParse(value, out _),
            _ => false
        };

    private static bool IsOptionalIdentifier(string? value) => value is null || IsIdentifier(value, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters);

    private static bool IsIdentifier(string? value, int maximumCharacters)
        => value is { Length: > 0 }
            && value.Length <= maximumCharacters
            && value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));

    private static bool IsReason(string? value)
        => value is { Length: > 0 and <= 128 }
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
