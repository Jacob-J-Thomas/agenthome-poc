using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers;

internal static class TriggerVocabulary
{
    internal static string ToCanonical(TriggerLoopTargetKind value) => value switch
    {
        TriggerLoopTargetKind.LegacyDefinition => "legacy-definition",
        TriggerLoopTargetKind.GovernedPublication => "governed-publication",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static bool TryParseLoopTargetKind(string? value, out TriggerLoopTargetKind kind)
    {
        kind = value switch
        {
            "legacy-definition" => TriggerLoopTargetKind.LegacyDefinition,
            "governed-publication" => TriggerLoopTargetKind.GovernedPublication,
            _ => TriggerLoopTargetKind.Unknown
        };
        return kind != TriggerLoopTargetKind.Unknown;
    }

    internal static string ToCanonical(TriggerKind value) => value switch
    {
        TriggerKind.Human => "human",
        TriggerKind.Webhook => "webhook",
        TriggerKind.FileChange => "file-change",
        TriggerKind.Message => "message",
        TriggerKind.Time => "time",
        TriggerKind.ToolOutput => "tool-output",
        TriggerKind.Loop => "loop",
        TriggerKind.System => "system",
        TriggerKind.MonitoredCondition => "monitored-condition",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static bool TryParseKind(string? value, out TriggerKind kind)
    {
        kind = value switch
        {
            "human" => TriggerKind.Human,
            "webhook" => TriggerKind.Webhook,
            "file-change" => TriggerKind.FileChange,
            "message" => TriggerKind.Message,
            "time" => TriggerKind.Time,
            "tool-output" => TriggerKind.ToolOutput,
            "loop" => TriggerKind.Loop,
            "system" => TriggerKind.System,
            "monitored-condition" => TriggerKind.MonitoredCondition,
            _ => TriggerKind.Unknown
        };
        return kind != TriggerKind.Unknown;
    }

    internal static string ToCanonical(TriggerAdmissionStatus value) => value switch
    {
        TriggerAdmissionStatus.Unknown => "unknown",
        TriggerAdmissionStatus.Admitted => "admitted",
        TriggerAdmissionStatus.Replayed => "replayed",
        TriggerAdmissionStatus.Conflicting => "conflicting",
        TriggerAdmissionStatus.NotYetEligible => "not-yet-eligible",
        TriggerAdmissionStatus.Expired => "expired",
        TriggerAdmissionStatus.Unauthorized => "unauthorized",
        TriggerAdmissionStatus.Unavailable => "unavailable",
        TriggerAdmissionStatus.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static bool TryParseStatus(string? value, out TriggerAdmissionStatus status)
    {
        status = value switch
        {
            "unknown" => TriggerAdmissionStatus.Unknown,
            "admitted" => TriggerAdmissionStatus.Admitted,
            "replayed" => TriggerAdmissionStatus.Replayed,
            "conflicting" => TriggerAdmissionStatus.Conflicting,
            "not-yet-eligible" => TriggerAdmissionStatus.NotYetEligible,
            "expired" => TriggerAdmissionStatus.Expired,
            "unauthorized" => TriggerAdmissionStatus.Unauthorized,
            "unavailable" => TriggerAdmissionStatus.Unavailable,
            "invalid" => TriggerAdmissionStatus.Invalid,
            _ => (TriggerAdmissionStatus)(-1)
        };
        return (int)status >= 0;
    }

    internal static string ToCanonical(TriggerAdmissionReason value) => value switch
    {
        TriggerAdmissionReason.Unknown => "unknown",
        TriggerAdmissionReason.EvidenceAccepted => "evidence-accepted",
        TriggerAdmissionReason.ExactReplay => "exact-replay",
        TriggerAdmissionReason.IdentityConflict => "identity-conflict",
        TriggerAdmissionReason.NotBefore => "not-before",
        TriggerAdmissionReason.DeadlineExceeded => "deadline-exceeded",
        TriggerAdmissionReason.Expired => "expired",
        TriggerAdmissionReason.InvalidEnvelope => "invalid-envelope",
        TriggerAdmissionReason.StaleLoop => "stale-loop",
        TriggerAdmissionReason.StaleAdapter => "stale-adapter",
        TriggerAdmissionReason.ActorMismatch => "actor-mismatch",
        TriggerAdmissionReason.SurfaceMismatch => "surface-mismatch",
        TriggerAdmissionReason.WorkspaceMismatch => "workspace-mismatch",
        TriggerAdmissionReason.RoleMismatch => "role-mismatch",
        TriggerAdmissionReason.AuthorityMismatch => "authority-mismatch",
        TriggerAdmissionReason.StaleAuthority => "stale-authority",
        TriggerAdmissionReason.AuthorityBoundary => "authority-boundary",
        TriggerAdmissionReason.StaleDelivery => "stale-delivery",
        TriggerAdmissionReason.AdapterUnavailable => "adapter-unavailable",
        TriggerAdmissionReason.HistoryUnavailable => "history-unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static bool TryParseReason(string? value, out TriggerAdmissionReason reason)
    {
        reason = value switch
        {
            "unknown" => TriggerAdmissionReason.Unknown,
            "evidence-accepted" => TriggerAdmissionReason.EvidenceAccepted,
            "exact-replay" => TriggerAdmissionReason.ExactReplay,
            "identity-conflict" => TriggerAdmissionReason.IdentityConflict,
            "not-before" => TriggerAdmissionReason.NotBefore,
            "deadline-exceeded" => TriggerAdmissionReason.DeadlineExceeded,
            "expired" => TriggerAdmissionReason.Expired,
            "invalid-envelope" => TriggerAdmissionReason.InvalidEnvelope,
            "stale-loop" => TriggerAdmissionReason.StaleLoop,
            "stale-adapter" => TriggerAdmissionReason.StaleAdapter,
            "actor-mismatch" => TriggerAdmissionReason.ActorMismatch,
            "surface-mismatch" => TriggerAdmissionReason.SurfaceMismatch,
            "workspace-mismatch" => TriggerAdmissionReason.WorkspaceMismatch,
            "role-mismatch" => TriggerAdmissionReason.RoleMismatch,
            "authority-mismatch" => TriggerAdmissionReason.AuthorityMismatch,
            "stale-authority" => TriggerAdmissionReason.StaleAuthority,
            "authority-boundary" => TriggerAdmissionReason.AuthorityBoundary,
            "stale-delivery" => TriggerAdmissionReason.StaleDelivery,
            "adapter-unavailable" => TriggerAdmissionReason.AdapterUnavailable,
            "history-unavailable" => TriggerAdmissionReason.HistoryUnavailable,
            _ => (TriggerAdmissionReason)(-1)
        };
        return (int)reason >= 0;
    }
}
