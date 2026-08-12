using System.Globalization;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Creates immutable trigger-delivery evidence only after closed, bounded validation.
/// </summary>
public static class TriggerDeliveryFactory
{
    /// <summary>
    /// Creates an exact legacy custom-loop definition reference.
    /// </summary>
    /// <param name="loopId">The stable custom-loop identifier.</param>
    /// <param name="definitionVersion">The exact positive definition version.</param>
    /// <param name="contentHash">The exact lowercase SHA-256 definition hash.</param>
    /// <param name="loop">The immutable loop reference when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the reference is valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateLoopReference(string? loopId, int definitionVersion, string? contentHash, out TriggerLoopReference? loop, out TriggerContractValidationResult validation)
    {
        loop = loopId is null || contentHash is null
            ? null
            : new TriggerLoopReference(
                TriggerLoopTargetKind.LegacyDefinition,
                new TriggerLegacyLoopDefinitionReference(loopId, definitionVersion, contentHash),
                null,
                null);
        validation = TriggerDeliveryValidator.ValidateLoopReference(loop);
        if (!validation.IsValid)
        {
            loop = null;
        }

        return validation.IsValid;
    }

    /// <summary>
    /// Creates an exact governed-loop publication and authority-grant reference.
    /// </summary>
    /// <param name="governedPublication">The exact immutable publication pin.</param>
    /// <param name="authorityGrant">The exact immutable authority-grant revision reference.</param>
    /// <param name="loop">The defensively copied immutable loop reference when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when both references are exact schema-1 contracts; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateGovernedLoopReference(GovernedLoopRevisionPublicationPin? governedPublication, AuthorityGrantReference? authorityGrant, out TriggerLoopReference? loop, out TriggerContractValidationResult validation)
    {
        loop = governedPublication is null || authorityGrant is null
            ? null
            : new TriggerLoopReference(TriggerLoopTargetKind.GovernedPublication, null, governedPublication, authorityGrant);
        validation = TriggerDeliveryValidator.ValidateLoopReference(loop);
        if (!validation.IsValid)
        {
            loop = null;
            return false;
        }

        if (!TryCopyGrant(authorityGrant!, out var grantCopy))
        {
            loop = null;
            validation = Failure("invalid_loop_reference", "loop.authorityGrant");
            return false;
        }

        var publicationCopy = GovernedLoopRevisionPublicationPinFactory.Create(
            governedPublication!.SchemaVersion,
            governedPublication.Revision,
            governedPublication.PublicationOperationId,
            governedPublication.ValidationEvidenceHash);
        loop = new TriggerLoopReference(TriggerLoopTargetKind.GovernedPublication, null, publicationCopy, grantCopy);
        return true;
    }

    /// <summary>
    /// Creates exact actor, surface, workspace, and role evidence.
    /// </summary>
    /// <param name="actorId">The exact reviewed authority actor identity.</param>
    /// <param name="surfaceId">The bounded canonical surface token.</param>
    /// <param name="workspaceId">The bounded canonical workspace token.</param>
    /// <param name="roleId">The bounded canonical role token.</param>
    /// <param name="context">The immutable context when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when every identity is valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateActorContext(AuthorityActorId? actorId, string? surfaceId, string? workspaceId, string? roleId, out TriggerActorContext? context, out TriggerContractValidationResult validation)
    {
        context = actorId is null || surfaceId is null || workspaceId is null || roleId is null ? null : new TriggerActorContext(actorId, surfaceId, workspaceId, roleId);
        validation = new TriggerContractValidationResult(context is null ? [Error("invalid_actor_context", "actorContext")] : ValidateActorContext(context));
        if (!validation.IsValid)
        {
            context = null;
        }

        return validation.IsValid;
    }

    /// <summary>
    /// Creates exact UTC temporal evidence without reading current time.
    /// </summary>
    /// <param name="observedAtUtc">When the adapter observed the event.</param>
    /// <param name="receivedAtUtc">When the harness received the delivery.</param>
    /// <param name="createdAtUtc">When the source created the delivery.</param>
    /// <param name="admittedAtUtc">The optional prior admission instant carried as evidence.</param>
    /// <param name="notBeforeUtc">The optional inclusive eligibility start.</param>
    /// <param name="deadlineUtc">The optional inclusive deadline.</param>
    /// <param name="expiresAtUtc">The optional exclusive-validity expiry instant.</param>
    /// <param name="temporal">The immutable evidence when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the exact chronology is valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateTemporalEvidence(DateTimeOffset observedAtUtc, DateTimeOffset receivedAtUtc, DateTimeOffset createdAtUtc, DateTimeOffset? admittedAtUtc, DateTimeOffset? notBeforeUtc, DateTimeOffset? deadlineUtc, DateTimeOffset? expiresAtUtc, out TriggerTemporalEvidence? temporal, out TriggerContractValidationResult validation)
    {
        temporal = new TriggerTemporalEvidence(observedAtUtc, receivedAtUtc, createdAtUtc, admittedAtUtc, notBeforeUtc, deadlineUtc, expiresAtUtc);
        validation = new TriggerContractValidationResult(TriggerDeliveryValidator.ValidateTemporal(temporal));
        if (!validation.IsValid)
        {
            temporal = null;
        }

        return validation.IsValid;
    }

    /// <summary>
    /// Creates immutable inline payload evidence and computes its exact digest.
    /// </summary>
    /// <param name="content">The bounded payload bytes, including an allowed empty payload.</param>
    /// <param name="payload">The immutable payload evidence when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the payload is within bounds; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateInlinePayload(byte[]? content, out TriggerPayloadEvidence? payload, out TriggerContractValidationResult validation)
    {
        if (content is null || content.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes)
        {
            payload = null;
            validation = Failure("inline_payload_out_of_range", "payload.inlineBase64");
            return false;
        }

        var snapshot = content.ToArray();
        payload = new TriggerPayloadEvidence(snapshot, null, CapabilityIntegrityDigest.Compute(snapshot));
        validation = Valid();
        return true;
    }

    /// <summary>
    /// Creates governed-reference payload evidence with an exact caller-supplied digest.
    /// </summary>
    /// <param name="governedReference">The canonical non-locator <c>payload/</c> reference.</param>
    /// <param name="contentHash">The exact content digest proved by the governed payload source.</param>
    /// <param name="payload">The immutable payload evidence when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when both reference and digest are valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateReferencedPayload(string? governedReference, CapabilityIntegrityDigest? contentHash, out TriggerPayloadEvidence? payload, out TriggerContractValidationResult validation)
    {
        if (!TriggerTextRules.IsGovernedPayloadReference(governedReference) || contentHash is null || !CapabilityIntegrityDigest.TryParse(contentHash.Value, out _, out _))
        {
            payload = null;
            validation = Failure("invalid_payload_reference", "payload.governedReference");
            return false;
        }

        payload = new TriggerPayloadEvidence(null, governedReference, contentHash);
        validation = Valid();
        return true;
    }

    /// <summary>
    /// Creates bounded redelivery evidence.
    /// </summary>
    /// <param name="attempt">The one-based attempt number.</param>
    /// <param name="count">The one-based total delivery count observed by the adapter.</param>
    /// <param name="originalDeliveryId">The stable original delivery identity.</param>
    /// <param name="redelivery">The immutable evidence when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the counters and identity are valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateRedeliveryEvidence(int attempt, int count, TriggerDeliveryId? originalDeliveryId, out TriggerRedeliveryEvidence? redelivery, out TriggerContractValidationResult validation)
    {
        if (originalDeliveryId is null || attempt is < 1 or > TriggerDeliveryLimits.MaxRedeliveryCount || count < attempt || count > TriggerDeliveryLimits.MaxRedeliveryCount)
        {
            redelivery = null;
            validation = Failure("invalid_redelivery", "redelivery");
            return false;
        }

        redelivery = new TriggerRedeliveryEvidence(attempt, count, originalDeliveryId);
        validation = Valid();
        return true;
    }

    /// <summary>
    /// Creates a canonical schema-version-1 envelope from validated evidence.
    /// </summary>
    /// <param name="schemaVersion">The exact supported schema version.</param>
    /// <param name="deliveryId">The stable delivery identity.</param>
    /// <param name="deduplicationId">The stable idempotency identity.</param>
    /// <param name="kind">The closed trigger source kind.</param>
    /// <param name="adapter">The exact reviewed capability and implementation pin.</param>
    /// <param name="loop">The exact loop definition pin.</param>
    /// <param name="actorContext">The exact actor and scope evidence.</param>
    /// <param name="authority">The exact non-executing authority evidence.</param>
    /// <param name="temporal">The exact temporal evidence.</param>
    /// <param name="payload">The bounded payload evidence.</param>
    /// <param name="redelivery">The bounded redelivery evidence.</param>
    /// <param name="publicationRequested">Whether later execution would request conversation publication.</param>
    /// <param name="invokingConversation">The exact conversation reference required for requested publication.</param>
    /// <param name="visibleStatus">The caller-visible status evidence.</param>
    /// <param name="visibleReason">The stable caller-visible reason evidence.</param>
    /// <param name="envelope">The immutable envelope when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when all evidence is bounded and consistent; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreateEnvelope(
        int schemaVersion,
        TriggerDeliveryId? deliveryId,
        TriggerDeduplicationId? deduplicationId,
        TriggerKind kind,
        TriggerAdapterReference? adapter,
        TriggerLoopReference? loop,
        TriggerActorContext? actorContext,
        TriggerAuthorityEvidence? authority,
        TriggerTemporalEvidence? temporal,
        TriggerPayloadEvidence? payload,
        TriggerRedeliveryEvidence? redelivery,
        bool publicationRequested,
        CustomLoopConversationReference? invokingConversation,
        TriggerAdmissionStatus visibleStatus,
        TriggerAdmissionReason visibleReason,
        out TriggerDeliveryEnvelope? envelope,
        out TriggerContractValidationResult validation)
        => TryCreateEnvelopeCore(
            schemaVersion,
            deliveryId,
            deduplicationId,
            kind,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            null,
            publicationRequested,
            invokingConversation,
            visibleStatus,
            visibleReason,
            out envelope,
            out validation);

    /// <summary>
    /// Creates a canonical schema-version-1 time envelope with exact schedule execution coordinates.
    /// </summary>
    /// <remarks>The directive is evidence only and is never interpreted as an authority grant.</remarks>
    public static bool TryCreateScheduledEnvelope(
        int schemaVersion,
        TriggerDeliveryId? deliveryId,
        TriggerDeduplicationId? deduplicationId,
        TriggerAdapterReference? adapter,
        TriggerLoopReference? loop,
        TriggerActorContext? actorContext,
        TriggerAuthorityEvidence? authority,
        TriggerTemporalEvidence? temporal,
        TriggerPayloadEvidence? payload,
        TriggerRedeliveryEvidence? redelivery,
        ScheduleExecutionDirective? scheduleExecutionDirective,
        bool publicationRequested,
        CustomLoopConversationReference? invokingConversation,
        TriggerAdmissionStatus visibleStatus,
        TriggerAdmissionReason visibleReason,
        out TriggerDeliveryEnvelope? envelope,
        out TriggerContractValidationResult validation)
        => TryCreateEnvelopeCore(
            schemaVersion,
            deliveryId,
            deduplicationId,
            TriggerKind.Time,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            scheduleExecutionDirective,
            publicationRequested,
            invokingConversation,
            visibleStatus,
            visibleReason,
            out envelope,
            out validation);

    private static bool TryCreateEnvelopeCore(
        int schemaVersion,
        TriggerDeliveryId? deliveryId,
        TriggerDeduplicationId? deduplicationId,
        TriggerKind kind,
        TriggerAdapterReference? adapter,
        TriggerLoopReference? loop,
        TriggerActorContext? actorContext,
        TriggerAuthorityEvidence? authority,
        TriggerTemporalEvidence? temporal,
        TriggerPayloadEvidence? payload,
        TriggerRedeliveryEvidence? redelivery,
        ScheduleExecutionDirective? scheduleExecutionDirective,
        bool publicationRequested,
        CustomLoopConversationReference? invokingConversation,
        TriggerAdmissionStatus visibleStatus,
        TriggerAdmissionReason visibleReason,
        out TriggerDeliveryEnvelope? envelope,
        out TriggerContractValidationResult validation)
    {
        var directiveSnapshot = ScheduleContractCopy.Copy(scheduleExecutionDirective);
        envelope = deliveryId is null || deduplicationId is null || adapter is null || loop is null || actorContext is null || authority is null || temporal is null || payload is null || redelivery is null
            ? null
            : new TriggerDeliveryEnvelope(schemaVersion, deliveryId, deduplicationId, kind, adapter, loop, actorContext, authority, temporal, payload, redelivery, directiveSnapshot, publicationRequested, invokingConversation, visibleStatus, visibleReason);
        validation = TriggerDeliveryValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            envelope = null;
            return false;
        }

        if (!TriggerDeliveryJson.TrySerializeKnownValid(envelope!, out _, out var serializationError))
        {
            envelope = null;
            validation = new TriggerContractValidationResult([serializationError!]);
            return false;
        }

        return true;
    }

    internal static bool TryCreateAuthorityEvidence(AuthorityProfileReference? profile, AuthorityBoundaryReceipt? receipt, out TriggerAuthorityEvidence? authority)
    {
        authority = null;
        if (profile?.ProfileId is null || profile.Revision is null || receipt is null || !AuthorityBoundaryReceiptFactory.Validate(receipt).IsValid)
        {
            return false;
        }

        if (!AuthorityBoundaryReceiptFactory.TryCreate(receipt.SchemaVersion, receipt.Decision, receipt.Conditions, receipt.Profiles, receipt.EvaluatedAtUtc, out var canonicalReceipt, out _) || !canonicalReceipt!.Profiles.Contains(profile))
        {
            return false;
        }

        authority = new TriggerAuthorityEvidence(profile, canonicalReceipt);
        return true;
    }

    private static IEnumerable<TriggerContractError> ValidateActorContext(TriggerActorContext context)
    {
        var errors = new List<TriggerContractError>();
        if (!AuthorityActorId.TryParse(context.ActorId.Value, out _, out _))
        {
            errors.Add(Error("invalid_actor", "actorContext.actorId"));
        }

        if (!TriggerTextRules.IsToken(context.SurfaceId, TriggerDeliveryLimits.MaxSurfaceIdCharacters))
        {
            errors.Add(Error("invalid_surface", "actorContext.surfaceId"));
        }

        if (!CustomLoopArtifactIdentifier.IsValid(context.WorkspaceId, TriggerDeliveryLimits.MaxWorkspaceIdCharacters))
        {
            errors.Add(Error("invalid_workspace", "actorContext.workspaceId"));
        }

        if (!CustomLoopArtifactIdentifier.IsValid(context.RoleId, TriggerDeliveryLimits.MaxRoleIdCharacters))
        {
            errors.Add(Error("invalid_role", "actorContext.roleId"));
        }

        return errors;
    }

    private static bool TryCopyGrant(AuthorityGrantReference grant, out AuthorityGrantReference? copy)
    {
        copy = null;
        if (!AuthorityGrantId.TryParse(grant.GrantId.Value, out var grantId, out _)
            || !AuthorityGrantRevision.TryParse(grant.Revision.Value.ToString(CultureInfo.InvariantCulture), out var revision, out _)
            || !AuthorityGrantHash.IsCanonical(grant.ContentHash))
        {
            return false;
        }

        copy = new AuthorityGrantReference(grantId!, revision!, grant.ContentHash);
        return true;
    }

    private static TriggerContractValidationResult Valid() => new([]);

    private static TriggerContractValidationResult Failure(string code, string field) => new([Error(code, field)]);

    private static TriggerContractError Error(string code, string field) => new(code, field);
}
