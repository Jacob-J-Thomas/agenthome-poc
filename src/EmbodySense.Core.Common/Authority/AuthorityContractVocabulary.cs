using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Authority;

internal static class AuthorityContractVocabulary
{
    internal static bool TryParseStatus(string? value, out AuthorityProfileStatus status)
    {
        status = value switch
        {
            "draft" => AuthorityProfileStatus.Draft,
            "active" => AuthorityProfileStatus.Active,
            "suspended" => AuthorityProfileStatus.Suspended,
            "retired" => AuthorityProfileStatus.Retired,
            _ => AuthorityProfileStatus.Unknown
        };
        return status != AuthorityProfileStatus.Unknown;
    }

    internal static bool TryParseProvenanceKind(string? value, out AuthorityProvenanceKind kind)
    {
        kind = value switch
        {
            "user-declaration" => AuthorityProvenanceKind.UserDeclaration,
            "imported-artifact" => AuthorityProvenanceKind.ImportedArtifact,
            "audit-replay" => AuthorityProvenanceKind.AuditReplay,
            _ => AuthorityProvenanceKind.Unknown
        };
        return kind != AuthorityProvenanceKind.Unknown;
    }

    internal static bool TryParseDecision(string? value, out AuthorityBoundaryDecision decision)
    {
        decision = value switch
        {
            "direct" => AuthorityBoundaryDecision.Direct,
            "review" => AuthorityBoundaryDecision.Review,
            "pause" => AuthorityBoundaryDecision.Pause,
            "deny" => AuthorityBoundaryDecision.Deny,
            _ => AuthorityBoundaryDecision.Unknown
        };
        return decision != AuthorityBoundaryDecision.Unknown;
    }

    internal static bool TryParseReason(string? value, out AuthorityBoundaryReason reason)
    {
        reason = value switch
        {
            "no-boundary" => AuthorityBoundaryReason.NoBoundary,
            "mandatory-review" => AuthorityBoundaryReason.MandatoryReview,
            "human-approval-required" => AuthorityBoundaryReason.HumanApprovalRequired,
            "profile-draft" => AuthorityBoundaryReason.ProfileDraft,
            "profile-suspended" => AuthorityBoundaryReason.ProfileSuspended,
            "profile-retired" => AuthorityBoundaryReason.ProfileRetired,
            "profile-expired" => AuthorityBoundaryReason.ProfileExpired,
            "invalid-contract" => AuthorityBoundaryReason.InvalidContract,
            "stale-evidence" => AuthorityBoundaryReason.StaleEvidence,
            "conflicting-state" => AuthorityBoundaryReason.ConflictingState,
            "uncertain-user-intent" => AuthorityBoundaryReason.UncertainUserIntent,
            "target-limit-exceeded" => AuthorityBoundaryReason.TargetLimitExceeded,
            "data-class-exceeded" => AuthorityBoundaryReason.DataClassExceeded,
            "side-effect-exceeded" => AuthorityBoundaryReason.SideEffectExceeded,
            "external-publication" => AuthorityBoundaryReason.ExternalPublication,
            "irreversible-action" => AuthorityBoundaryReason.IrreversibleAction,
            "recurrence" => AuthorityBoundaryReason.Recurrence,
            _ => AuthorityBoundaryReason.Unknown
        };
        return reason != AuthorityBoundaryReason.Unknown;
    }

    internal static bool TryParseSideEffectClass(string? value, out CapabilitySideEffectClass sideEffectClass)
    {
        sideEffectClass = value switch
        {
            "none" => CapabilitySideEffectClass.None,
            "read-only" => CapabilitySideEffectClass.ReadOnly,
            "local-reversible" => CapabilitySideEffectClass.LocalReversible,
            "external-reversible" => CapabilitySideEffectClass.ExternalReversible,
            "irreversible" => CapabilitySideEffectClass.Irreversible,
            _ => CapabilitySideEffectClass.Unknown
        };
        return sideEffectClass != CapabilitySideEffectClass.Unknown;
    }

    internal static string ToCanonical(AuthorityProfileStatus value) => value switch
    {
        AuthorityProfileStatus.Draft => "draft",
        AuthorityProfileStatus.Active => "active",
        AuthorityProfileStatus.Suspended => "suspended",
        AuthorityProfileStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static string ToCanonical(AuthorityProvenanceKind value) => value switch
    {
        AuthorityProvenanceKind.UserDeclaration => "user-declaration",
        AuthorityProvenanceKind.ImportedArtifact => "imported-artifact",
        AuthorityProvenanceKind.AuditReplay => "audit-replay",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static string ToCanonical(AuthorityBoundaryDecision value) => value switch
    {
        AuthorityBoundaryDecision.Direct => "direct",
        AuthorityBoundaryDecision.Review => "review",
        AuthorityBoundaryDecision.Pause => "pause",
        AuthorityBoundaryDecision.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static string ToCanonical(AuthorityBoundaryReason value) => value switch
    {
        AuthorityBoundaryReason.NoBoundary => "no-boundary",
        AuthorityBoundaryReason.MandatoryReview => "mandatory-review",
        AuthorityBoundaryReason.HumanApprovalRequired => "human-approval-required",
        AuthorityBoundaryReason.ProfileDraft => "profile-draft",
        AuthorityBoundaryReason.ProfileSuspended => "profile-suspended",
        AuthorityBoundaryReason.ProfileRetired => "profile-retired",
        AuthorityBoundaryReason.ProfileExpired => "profile-expired",
        AuthorityBoundaryReason.InvalidContract => "invalid-contract",
        AuthorityBoundaryReason.StaleEvidence => "stale-evidence",
        AuthorityBoundaryReason.ConflictingState => "conflicting-state",
        AuthorityBoundaryReason.UncertainUserIntent => "uncertain-user-intent",
        AuthorityBoundaryReason.TargetLimitExceeded => "target-limit-exceeded",
        AuthorityBoundaryReason.DataClassExceeded => "data-class-exceeded",
        AuthorityBoundaryReason.SideEffectExceeded => "side-effect-exceeded",
        AuthorityBoundaryReason.ExternalPublication => "external-publication",
        AuthorityBoundaryReason.IrreversibleAction => "irreversible-action",
        AuthorityBoundaryReason.Recurrence => "recurrence",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static string ToCanonical(CapabilitySideEffectClass value) => value switch
    {
        CapabilitySideEffectClass.None => "none",
        CapabilitySideEffectClass.ReadOnly => "read-only",
        CapabilitySideEffectClass.LocalReversible => "local-reversible",
        CapabilitySideEffectClass.ExternalReversible => "external-reversible",
        CapabilitySideEffectClass.Irreversible => "irreversible",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
