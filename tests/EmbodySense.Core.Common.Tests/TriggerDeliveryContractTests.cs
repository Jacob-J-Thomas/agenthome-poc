using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class TriggerDeliveryContractTests
{
    [Fact]
    public void Canonical_json_round_trips_hashes_and_remains_culture_independent()
    {
        var conversation = new CustomLoopConversationReference("conversation-1", "version-1", TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2));
        var envelope = TriggerDeliveryTestData.Envelope(publicationRequested: true, conversation: conversation);
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(TriggerDeliveryJson.TrySerialize(envelope, out var json, out var serializeValidation));
            Assert.True(serializeValidation.IsValid);
            Assert.True(TriggerDeliveryJson.TryDeserialize(json, out var parsed, out var parseValidation));
            Assert.True(parseValidation.IsValid);
            Assert.True(TriggerDeliveryJson.TrySerialize(parsed, out var roundTrip, out _));
            Assert.Equal(json, roundTrip);
            Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var firstHash, out _));
            Assert.True(TriggerDeliveryHash.TryCompute(parsed, out var secondHash, out _));
            Assert.Equal(firstHash, secondHash);
            Assert.Matches("^[0-9a-f]{64}$", firstHash!);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    [Fact]
    public void Canonical_identity_is_invariant_to_permitted_authority_enumeration_order()
    {
        var left = TriggerDeliveryTestData.Envelope(authority: TriggerDeliveryTestData.Authority(reverseProfiles: false));
        var right = TriggerDeliveryTestData.Envelope(authority: TriggerDeliveryTestData.Authority(reverseProfiles: true));

        Assert.True(TriggerDeliveryHash.TryCompute(left, out var leftHash, out _));
        Assert.True(TriggerDeliveryHash.TryCompute(right, out var rightHash, out _));
        Assert.Equal(leftHash, rightHash);
    }

    [Fact]
    public void Canonical_hash_changes_for_every_identity_authority_target_payload_and_temporal_field()
    {
        var baseline = TriggerDeliveryTestData.Envelope();
        Assert.True(TriggerDeliveryHash.TryCompute(baseline, out var baselineHash, out _));
        var variants = new[]
        {
            TriggerDeliveryTestData.Envelope(deliveryId: "delivery-2"),
            TriggerDeliveryTestData.Envelope(deduplicationId: "dedup-2"),
            TriggerDeliveryTestData.Envelope(kind: TriggerKind.Message),
            TriggerDeliveryTestData.Envelope(adapter: TriggerDeliveryTestData.Adapter(id: "org.embodysense/triggers/message")),
            TriggerDeliveryTestData.Envelope(adapter: TriggerDeliveryTestData.Adapter(version: "1.0.1")),
            TriggerDeliveryTestData.Envelope(adapter: TriggerDeliveryTestData.Adapter(hashCharacter: "d")),
            TriggerDeliveryTestData.Envelope(adapter: TriggerDeliveryTestData.Adapter(implementation: "triggers/message")),
            TriggerDeliveryTestData.Envelope(adapter: TriggerDeliveryTestData.Adapter(provider: "example.adapters")),
            TriggerDeliveryTestData.Envelope(loop: TriggerDeliveryTestData.Loop(id: "loop-2")),
            TriggerDeliveryTestData.Envelope(loop: TriggerDeliveryTestData.Loop(version: 4)),
            TriggerDeliveryTestData.Envelope(loop: TriggerDeliveryTestData.Loop(hashCharacter: 'c')),
            TriggerDeliveryTestData.Envelope(actorContext: TriggerDeliveryTestData.ActorContext(actor: "other")),
            TriggerDeliveryTestData.Envelope(actorContext: TriggerDeliveryTestData.ActorContext(surface: "web")),
            TriggerDeliveryTestData.Envelope(actorContext: TriggerDeliveryTestData.ActorContext(workspace: "workspace-2")),
            TriggerDeliveryTestData.Envelope(actorContext: TriggerDeliveryTestData.ActorContext(role: "reviewer")),
            TriggerDeliveryTestData.Envelope(authority: TriggerDeliveryTestData.Authority(revisionValue: 8)),
            TriggerDeliveryTestData.Envelope(payload: TriggerDeliveryTestData.InlinePayload([9])),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(createdAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(1), observedAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2), receivedAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(3))),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(observedAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2), receivedAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(3))),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(receivedAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(3))),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(notBeforeUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(3))),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(deadlineUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(4))),
            TriggerDeliveryTestData.Envelope(temporal: TriggerDeliveryTestData.Temporal(expiresAtUtc: TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(5))),
            TriggerDeliveryTestData.Envelope(payload: TriggerDeliveryTestData.ReferencedPayload()),
            TriggerDeliveryTestData.Envelope(visibleStatus: TriggerAdmissionStatus.Invalid, visibleReason: TriggerAdmissionReason.InvalidEnvelope),
            TriggerDeliveryTestData.Envelope(publicationRequested: true, conversation: new CustomLoopConversationReference("conversation", "v1", TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2)))
        };

        foreach (var variant in variants)
        {
            Assert.True(TriggerDeliveryHash.TryCompute(variant, out var hash, out _));
            Assert.NotEqual(baselineHash, hash);
        }
    }

    [Fact]
    public void Parser_rejects_duplicate_reordered_malformed_unsafe_and_payload_mismatch_documents()
    {
        Assert.True(TriggerDeliveryJson.TrySerialize(TriggerDeliveryTestData.Envelope(), out var canonical, out _));
        var duplicate = canonical!.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        var reordered = canonical.Replace("\"deduplicationId\":\"dedup-1\",\"deliveryId\":\"delivery-1\"", "\"deliveryId\":\"delivery-1\",\"deduplicationId\":\"dedup-1\"", StringComparison.Ordinal);
        var payloadMismatch = canonical.Replace(TriggerDeliveryTestData.InlinePayload().ContentHash.Value, "sha256:" + new string('f', 64), StringComparison.Ordinal);
        var unsafeUnicode = canonical.Replace("delivery-1", "delivery-\u200b1", StringComparison.Ordinal);

        AssertRejected(duplicate, "invalid_json_shape");
        AssertRejected(reordered, "noncanonical_json");
        AssertRejected("{", "invalid_json");
        AssertRejected(unsafeUnicode, "invalid_json");
        AssertRejected(payloadMismatch, "invalid_json_shape");
        AssertRejected(new string(' ', TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes), "invalid_json");
        AssertRejected(new string(' ', TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes + 1), "invalid_json");
    }

    [Fact]
    public void Every_string_and_numeric_bound_accepts_the_boundary_and_rejects_boundary_plus_one()
    {
        Assert.True(TriggerDeliveryId.TryParse(new string('a', TriggerDeliveryLimits.MaxDeliveryIdCharacters), out _));
        Assert.False(TriggerDeliveryId.TryParse(new string('a', TriggerDeliveryLimits.MaxDeliveryIdCharacters + 1), out _));
        Assert.True(TriggerDeduplicationId.TryParse(new string('a', TriggerDeliveryLimits.MaxDeduplicationIdCharacters), out _));
        Assert.False(TriggerDeduplicationId.TryParse(new string('a', TriggerDeliveryLimits.MaxDeduplicationIdCharacters + 1), out _));
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(new string('a', TriggerDeliveryLimits.MaxLoopIdCharacters), TriggerDeliveryLimits.MaxLoopDefinitionVersion, new string('a', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference(new string('a', TriggerDeliveryLimits.MaxLoopIdCharacters + 1), TriggerDeliveryLimits.MaxLoopDefinitionVersion, new string('a', 64), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateLoopReference("loop", TriggerDeliveryLimits.MaxLoopDefinitionVersion + 1, new string('a', 64), out _, out _));

        Assert.True(AuthorityActorId.TryParse("actor", out var actor, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actor, new string('a', TriggerDeliveryLimits.MaxSurfaceIdCharacters), new string('a', TriggerDeliveryLimits.MaxWorkspaceIdCharacters), new string('a', TriggerDeliveryLimits.MaxRoleIdCharacters), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateActorContext(actor, new string('a', TriggerDeliveryLimits.MaxSurfaceIdCharacters + 1), "workspace", "role", out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateActorContext(actor, "surface", new string('a', TriggerDeliveryLimits.MaxWorkspaceIdCharacters + 1), "role", out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateActorContext(actor, "surface", "workspace", new string('a', TriggerDeliveryLimits.MaxRoleIdCharacters + 1), out _, out _));

        Assert.True(TriggerDeliveryFactory.TryCreateReferencedPayload("payload/" + new string('a', TriggerDeliveryLimits.MaxPayloadReferenceCharacters - "payload/".Length), EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute([1]), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateReferencedPayload("payload/" + new string('a', TriggerDeliveryLimits.MaxPayloadReferenceCharacters - "payload/".Length + 1), EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute([1]), out _, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes], out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateInlinePayload(new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes + 1], out _, out _));

        Assert.True(TriggerDeliveryId.TryParse("original", out var original));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(TriggerDeliveryLimits.MaxRedeliveryCount, TriggerDeliveryLimits.MaxRedeliveryCount, original, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(TriggerDeliveryLimits.MaxRedeliveryCount + 1, TriggerDeliveryLimits.MaxRedeliveryCount + 1, original, out _, out _));

        var boundaryConversation = new CustomLoopConversationReference(new string('a', TriggerDeliveryLimits.MaxConversationIdCharacters), new string('v', TriggerDeliveryLimits.MaxConversationVersionCharacters), TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2));
        Assert.NotNull(TriggerDeliveryTestData.Envelope(publicationRequested: true, conversation: boundaryConversation));
        var oversizedConversation = boundaryConversation with { ConversationId = new string('a', TriggerDeliveryLimits.MaxConversationIdCharacters + 1) };
        AssertEnvelopeRejected(publicationRequested: true, conversation: oversizedConversation, expectedCode: "invalid_conversation_reference");
        oversizedConversation = boundaryConversation with { CapturedVersion = new string('v', TriggerDeliveryLimits.MaxConversationVersionCharacters + 1) };
        AssertEnvelopeRejected(publicationRequested: true, conversation: oversizedConversation, expectedCode: "invalid_conversation_reference");
    }

    [Fact]
    public void Temporal_evidence_accepts_equal_and_horizon_endpoints_but_rejects_reordering_and_horizon_plus_one()
    {
        var created = TriggerDeliveryTestData.CreatedAtUtc;
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(created, created, created, created, created, created.AddDays(30), created.AddDays(30), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created, created, created, null, null, null, created.AddDays(30).AddTicks(1), out _, out var horizonValidation));
        Assert.Contains(horizonValidation.Errors, error => error.Code == "temporal_horizon_exceeded");
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(2), created.AddSeconds(1), created, null, null, null, null, out _, out var reorderedValidation));
        Assert.Contains(reorderedValidation.Errors, error => error.Code == "reordered_temporal_evidence");
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created, created, created, null, created.AddSeconds(2), created.AddSeconds(1), null, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created, created, created, null, created.AddSeconds(2), null, created.AddSeconds(2), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created, created, created, null, null, created.AddSeconds(2), created.AddSeconds(1), out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.ToOffset(TimeSpan.FromHours(1)), created, created, null, null, null, null, out _, out _));
    }

    [Fact]
    public void Temporal_evaluator_applies_exact_not_before_deadline_and_expiry_endpoints()
    {
        var created = TriggerDeliveryTestData.CreatedAtUtc;
        var evidence = TriggerDeliveryTestData.Temporal(observedAtUtc: created, receivedAtUtc: created, notBeforeUtc: created.AddSeconds(1), deadlineUtc: created.AddSeconds(2), expiresAtUtc: created.AddSeconds(3));
        Assert.Equal(TriggerTemporalState.NotYetEligible, TriggerTemporalEvaluator.Evaluate(evidence, created.AddSeconds(1).AddTicks(-1)));
        Assert.Equal(TriggerTemporalState.Eligible, TriggerTemporalEvaluator.Evaluate(evidence, created.AddSeconds(1)));
        Assert.Equal(TriggerTemporalState.Eligible, TriggerTemporalEvaluator.Evaluate(evidence, created.AddSeconds(2)));
        Assert.Equal(TriggerTemporalState.DeadlineExceeded, TriggerTemporalEvaluator.Evaluate(evidence, created.AddSeconds(2).AddTicks(1)));
        Assert.Equal(TriggerTemporalState.Expired, TriggerTemporalEvaluator.Evaluate(evidence, created.AddSeconds(3)));
        Assert.Equal(TriggerTemporalState.Unknown, TriggerTemporalEvaluator.Evaluate(evidence, created.ToOffset(TimeSpan.FromHours(1))));
        Assert.Equal(TriggerTemporalState.Unknown, TriggerTemporalEvaluator.Evaluate(null, created));
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted)]
    [InlineData(TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay)]
    public void Visible_admission_time_must_be_inside_exact_eligibility_endpoints(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        var created = TriggerDeliveryTestData.CreatedAtUtc;
        var notBefore = created.AddSeconds(3);
        var deadline = created.AddSeconds(5);
        var expiry = created.AddSeconds(6);

        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, notBefore.AddTicks(-1), notBefore, deadline, expiry, out _, out var beforeValidation));
        Assert.Contains(beforeValidation.Errors, error => error.Code == "admission_before_not_before");

        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, notBefore, notBefore, deadline, expiry, out var atNotBefore, out _));
        Assert.NotNull(TriggerDeliveryTestData.Envelope(temporal: atNotBefore, visibleStatus: status, visibleReason: reason));

        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, deadline, notBefore, deadline, expiry, out var atDeadline, out _));
        Assert.NotNull(TriggerDeliveryTestData.Envelope(temporal: atDeadline, visibleStatus: status, visibleReason: reason));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, deadline.AddTicks(1), notBefore, deadline, expiry, out _, out var afterDeadlineValidation));
        Assert.Contains(afterDeadlineValidation.Errors, error => error.Code == "admission_after_deadline");

        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, expiry.AddTicks(-1), notBefore, null, expiry, out var beforeExpiry, out _));
        Assert.NotNull(TriggerDeliveryTestData.Envelope(temporal: beforeExpiry, visibleStatus: status, visibleReason: reason));
        Assert.False(TriggerDeliveryFactory.TryCreateTemporalEvidence(created.AddSeconds(1), created.AddSeconds(2), created, expiry, notBefore, null, expiry, out _, out var atExpiryValidation));
        Assert.Contains(atExpiryValidation.Errors, error => error.Code == "admission_at_or_after_expiry");
    }

    [Fact]
    public void Payload_bytes_are_snapshotted_and_private_or_credential_like_references_are_rejected()
    {
        var source = new byte[] { 1, 2, 3 };
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(source, out var payload, out _));
        Assert.True(payload!.IsInline);
        source[0] = 9;
        var firstRead = payload.GetInlinePayload()!;
        firstRead[1] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.GetInlinePayload());
        Assert.False(TriggerDeliveryFactory.TryCreateReferencedPayload("https://user:secret@example.test/payload", payload.ContentHash, out _, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateReferencedPayload("secret/api-key", payload.ContentHash, out _, out _));
        Assert.False(TriggerDeliveryTestData.ReferencedPayload().IsInline);
        Assert.DoesNotContain(typeof(TriggerDeliveryEnvelope).GetProperties(), property => property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Catalog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Closed_kind_status_and_reason_vocabularies_round_trip_every_supported_value()
    {
        foreach (var kind in Enum.GetValues<TriggerKind>().Where(value => value != TriggerKind.Unknown))
        {
            AssertRoundTrips(TriggerDeliveryTestData.Envelope(kind: kind));
        }

        var pairs = new[]
        {
            (TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown),
            (TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted),
            (TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay),
            (TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict),
            (TriggerAdmissionStatus.NotYetEligible, TriggerAdmissionReason.NotBefore),
            (TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded),
            (TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleLoop),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAdapter),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.ActorMismatch),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.SurfaceMismatch),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.WorkspaceMismatch),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.RoleMismatch),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityMismatch),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityBoundary),
            (TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleDelivery),
            (TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable),
            (TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.HistoryUnavailable),
            (TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope)
        };
        foreach (var (status, reason) in pairs)
        {
            AssertRoundTrips(TriggerDeliveryTestData.Envelope(visibleStatus: status, visibleReason: reason));
        }
    }

    [Fact]
    public void Strong_delivery_and_deduplication_identities_use_ordinal_value_semantics()
    {
        Assert.True(TriggerDeliveryId.TryParse("delivery-a", out var deliveryA));
        Assert.True(TriggerDeliveryId.TryParse("delivery-a", out var deliveryACopy));
        Assert.True(TriggerDeliveryId.TryParse("delivery-b", out var deliveryB));
        Assert.Equal(0, deliveryA!.CompareTo(deliveryACopy));
        Assert.True(deliveryA.CompareTo(deliveryB) < 0);
        Assert.Equal(1, deliveryA.CompareTo(null));
        Assert.True(deliveryA.Equals(deliveryACopy));
        Assert.False(deliveryA.Equals(deliveryB));
        Assert.False(deliveryA.Equals((object?)"delivery-a"));
        Assert.Equal(deliveryA.GetHashCode(), deliveryACopy!.GetHashCode());
        Assert.Equal("delivery-a", deliveryA.ToString());

        Assert.True(TriggerDeduplicationId.TryParse("dedup-a", out var dedupA));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-a", out var dedupACopy));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-b", out var dedupB));
        Assert.Equal(0, dedupA!.CompareTo(dedupACopy));
        Assert.True(dedupA.CompareTo(dedupB) < 0);
        Assert.Equal(1, dedupA.CompareTo(null));
        Assert.True(dedupA.Equals(dedupACopy));
        Assert.False(dedupA.Equals(dedupB));
        Assert.False(dedupA.Equals((object?)"dedup-a"));
        Assert.Equal(dedupA.GetHashCode(), dedupACopy!.GetHashCode());
        Assert.Equal("dedup-a", dedupA.ToString());
        Assert.False(TriggerDeliveryId.TryParse(null, out _));
        Assert.False(TriggerDeliveryId.TryParse("-bad", out _));
        Assert.False(TriggerDeliveryId.TryParse("bad-", out _));
        Assert.False(TriggerDeliveryId.TryParse("bad/slash", out _));
        Assert.False(TriggerDeduplicationId.TryParse(string.Empty, out _));
    }

    [Fact]
    public void Safe_text_validation_rejects_unsafe_unicode_and_accepts_well_formed_normalized_supplementary_text()
    {
        var conversation = new CustomLoopConversationReference("conversation", "v😀", TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2));
        AssertRoundTrips(TriggerDeliveryTestData.Envelope(publicationRequested: true, conversation: conversation));
        foreach (var value in new[] { "v\ud800", "v\udc00", "v\u200b", "v\ufdd0", "e\u0301", "v\u0001" })
        {
            var candidate = new CustomLoopConversationReference("conversation", value, TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2));
            AssertEnvelopeRejected(publicationRequested: true, conversation: candidate, expectedCode: "invalid_conversation_reference");
        }
    }

    [Fact]
    public void Forged_adapter_authority_schema_kind_redelivery_and_outcome_evidence_fail_closed()
    {
        Assert.True(TriggerDeliveryId.TryParse("delivery-1", out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-1", out var deduplication));
        Assert.True(TriggerDeliveryId.TryParse("other", out var other));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, other, out var wrongOriginal, out _));
        var forgedAdapter = new TriggerAdapterReference(null!, new EmbodySense.Core.Common.Capabilities.Models.CapabilityImplementationIdentity(null!, "../secret"));
        var malformedAdapter = TriggerDeliveryTestData.Adapter() with { Implementation = TriggerDeliveryTestData.Adapter().Implementation with { ImplementationId = "../secret" } };
        var otherAuthority = TriggerDeliveryTestData.Authority(revisionValue: 8);
        var forgedAuthority = new TriggerAuthorityEvidence(otherAuthority.Profile, TriggerDeliveryTestData.Authority().BoundaryReceipt);
        var missingAuthority = new TriggerAuthorityEvidence(null!, null!);

        AssertCreateRejected(schemaVersion: 2, delivery!, deduplication!, TriggerKind.Webhook, TriggerDeliveryTestData.Adapter(), TriggerDeliveryTestData.Authority(), null, "unsupported_schema_version");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Unknown, TriggerDeliveryTestData.Adapter(), TriggerDeliveryTestData.Authority(), null, "unsupported_trigger_kind");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Webhook, forgedAdapter, TriggerDeliveryTestData.Authority(), null, "invalid_adapter_reference");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Webhook, malformedAdapter, TriggerDeliveryTestData.Authority(), null, "invalid_adapter_reference");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Webhook, TriggerDeliveryTestData.Adapter(), forgedAuthority, null, "invalid_authority_evidence");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Webhook, TriggerDeliveryTestData.Adapter(), missingAuthority, null, "invalid_authority_evidence");
        AssertCreateRejected(schemaVersion: 1, delivery!, deduplication!, TriggerKind.Webhook, TriggerDeliveryTestData.Adapter(), TriggerDeliveryTestData.Authority(), wrongOriginal, "invalid_original_delivery");
        AssertEnvelopeRejected(visibleStatus: TriggerAdmissionStatus.Admitted, visibleReason: TriggerAdmissionReason.ExactReplay, expectedCode: "invalid_visible_outcome");
        Assert.False(TriggerDeliveryHash.TryCompute(null, out var hash, out var validation));
        Assert.Null(hash);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Publication_requires_one_bounded_exact_conversation_reference_and_status_never_repairs_missing_admission_time()
    {
        AssertEnvelopeRejected(publicationRequested: true, conversation: null, expectedCode: "conversation_reference_required");
        var conversation = new CustomLoopConversationReference("conversation", "version", TriggerDeliveryTestData.CreatedAtUtc.AddSeconds(2));
        AssertEnvelopeRejected(publicationRequested: false, conversation: conversation, expectedCode: "conversation_reference_required");

        var temporal = TriggerDeliveryTestData.Temporal(admittedAtUtc: null);
        AssertEnvelopeRejected(temporal: temporal, visibleStatus: TriggerAdmissionStatus.Admitted, visibleReason: TriggerAdmissionReason.EvidenceAccepted, expectedCode: "invalid_admitted_time");
    }

    private static void AssertRejected(string json, string expectedCode)
    {
        Assert.False(TriggerDeliveryJson.TryDeserialize(json, out var envelope, out var validation));
        Assert.Null(envelope);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private static void AssertRoundTrips(TriggerDeliveryEnvelope envelope)
    {
        Assert.True(TriggerDeliveryJson.TrySerialize(envelope, out var json, out _));
        Assert.True(TriggerDeliveryJson.TryDeserialize(json, out _, out _));
    }

    private static void AssertCreateRejected(int schemaVersion, TriggerDeliveryId delivery, TriggerDeduplicationId deduplication, TriggerKind kind, TriggerAdapterReference adapter, TriggerAuthorityEvidence authority, TriggerRedeliveryEvidence? redelivery, string expectedCode)
    {
        redelivery ??= CreateRedelivery(delivery);
        Assert.False(TriggerDeliveryFactory.TryCreateEnvelope(schemaVersion, delivery, deduplication, kind, adapter, TriggerDeliveryTestData.Loop(), TriggerDeliveryTestData.ActorContext(), authority, TriggerDeliveryTestData.Temporal(), TriggerDeliveryTestData.InlinePayload(), redelivery, false, null, TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, out _, out var validation));
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private static TriggerRedeliveryEvidence CreateRedelivery(TriggerDeliveryId delivery)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out var redelivery, out _));
        return redelivery!;
    }

    private static void AssertEnvelopeRejected(bool publicationRequested = false, CustomLoopConversationReference? conversation = null, TriggerTemporalEvidence? temporal = null, TriggerAdmissionStatus visibleStatus = TriggerAdmissionStatus.Unknown, TriggerAdmissionReason visibleReason = TriggerAdmissionReason.Unknown, string expectedCode = "invalid_conversation_reference")
    {
        Assert.True(TriggerDeliveryId.TryParse("delivery-1", out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-1", out var deduplication));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out var redelivery, out _));
        Assert.False(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, TriggerKind.Webhook, TriggerDeliveryTestData.Adapter(), TriggerDeliveryTestData.Loop(), TriggerDeliveryTestData.ActorContext(), TriggerDeliveryTestData.Authority(), temporal ?? TriggerDeliveryTestData.Temporal(), TriggerDeliveryTestData.InlinePayload(), redelivery, publicationRequested, conversation, visibleStatus, visibleReason, out var envelope, out var validation));
        Assert.Null(envelope);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }
}
