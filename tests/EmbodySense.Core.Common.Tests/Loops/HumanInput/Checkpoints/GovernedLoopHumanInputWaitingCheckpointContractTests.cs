using System.Text.Json.Nodes;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.HumanInput.Checkpoints;

public sealed class GovernedLoopHumanInputWaitingCheckpointContractTests
{
    [Fact]
    public void Exact_resolved_policy_scope_hash_and_request_window_are_checkpoint_bound()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var malformedPolicy = pending.ResolvedPolicy with { NodeId = "other-node" };
        var malformed = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, malformedPolicy, pending.Request, pending.Posture, pending.Evidence, string.Empty));
        var policy = pending.ResolvedPolicy;
        var secretPolicy = new HumanInputPolicyResolutionSnapshot(policy.SchemaVersion, policy.WorkspaceId, policy.GraphId, policy.GraphRevisionId, policy.NodeId, policy.ActorId, HumanInputPolicyArtifactHash.Apply(policy.TimeoutPolicy with { PolicyId = "ghp_fake" }), policy.FailurePolicy, policy.ResolvedAtUtc, policy.ExpiresAtUtc, policy.TerminalDisposition, policy.ResolutionHash);
        var secret = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, secretPolicy, pending.Request, pending.Posture, pending.Evidence, string.Empty));
        var retimedRequest = HumanInputRequestHash.Apply(pending.Request with { Timing = new HumanInputTiming(pending.Request.Timing.RequestedAtUtc, pending.Request.Timing.ExpiresAtUtc.AddMinutes(-1)), RequestHash = string.Empty });
        var retimed = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, pending.ResolvedPolicy, retimedRequest, pending.Posture, pending.Evidence, string.Empty));

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(pending).IsValid);
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(malformed).Errors, error => error.Code == "invalid_resolved_policy");
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(secret).Errors, error => error.Code == "invalid_resolved_policy");
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(retimed).Errors, error => error.Code == "request_policy_timing_mismatch");
    }

    [Fact]
    public void Every_closed_posture_is_valid_and_only_legal_single_boundary_transitions_are_admitted()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var answered = GovernedLoopHumanInputWaitingCheckpointTestData.Answered(pending);
        var expired = GovernedLoopHumanInputWaitingCheckpointTestData.Expired(pending);
        var cancelled = GovernedLoopHumanInputWaitingCheckpointTestData.Cancelled(pending);
        var superseded = GovernedLoopHumanInputWaitingCheckpointTestData.Superseded(pending);
        var terminal = GovernedLoopHumanInputWaitingCheckpointTestData.Terminal(answered);

        Assert.All(new[] { pending, answered, expired, cancelled, superseded, terminal }, checkpoint =>
        {
            var validation = GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Code)));
        });
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(null, pending).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, answered).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, expired).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, cancelled).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, superseded).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(answered, terminal).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(terminal, terminal).IsValid);
        Assert.False(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, terminal).IsValid);
        Assert.False(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(expired, pending).IsValid);
    }

    [Fact]
    public void Expiration_requires_evidence_strictly_after_the_inclusive_response_endpoint()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var expired = GovernedLoopHumanInputWaitingCheckpointTestData.Expired(pending);
        var atEndpointEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(expired.Evidence[1] with { OccurredAtUtc = pending.Request.Timing.ExpiresAtUtc });
        var atEndpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, pending.ResolvedPolicy, pending.Request, GovernedLoopHumanInputWaitingCheckpointPosture.Expired, [pending.Evidence[0], atEndpointEvidence], string.Empty));

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(expired).IsValid);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, expired).IsValid);
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(atEndpoint).Errors, error => error.Code == "expired_at_or_before_deadline");
        Assert.False(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, atEndpoint).IsValid);
    }

    [Fact]
    public void Canonical_json_restarts_exactly_and_rejects_unknown_malformed_or_noncanonical_artifacts()
    {
        var checkpoint = GovernedLoopHumanInputWaitingCheckpointTestData.Terminal();

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TrySerialize(checkpoint, out var json, out _));
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(json, out var restarted, out _));
        Assert.Equal(checkpoint.CheckpointHash, restarted!.CheckpointHash);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.ExactReplay, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(checkpoint, restarted));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(json + " ", out _, out _));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(json!.Replace("checkpointHash", "unknownHash", StringComparison.Ordinal), out _, out _));
        var rootSchemaVersionIndex = json.LastIndexOf("\"schemaVersion\":1", StringComparison.Ordinal);
        var unsupportedJson = string.Concat(json.AsSpan(0, rootSchemaVersionIndex), "\"schemaVersion\":2", json.AsSpan(rootSchemaVersionIndex + "\"schemaVersion\":1".Length));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(unsupportedJson, out _, out var unsupported));
        Assert.Contains(unsupported.Errors, error => error.Code == "unsupported_schema_version");
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize("{", out _, out _));
    }

    [Theory]
    [InlineData("timeoutPolicy")]
    [InlineData("failurePolicy")]
    public void Malformed_resolved_policy_artifacts_fail_closed_with_contract_errors(string propertyName)
    {
        var checkpoint = GovernedLoopHumanInputWaitingCheckpointTestData.Terminal();
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TrySerialize(checkpoint, out var json, out _));
        var document = JsonNode.Parse(json!)!.AsObject();
        document["resolvedPolicy"]!.AsObject()[propertyName] = new JsonArray();

        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(document.ToJsonString(), out var restarted, out var validation));
        Assert.Null(restarted);
        Assert.Contains(validation.Errors, error => error.Code == "invalid_json_type" && error.Path == "$.resolvedPolicy." + propertyName);
    }

    [Fact]
    public void Canonical_json_roundtrips_every_supported_response_schema()
    {
        var configurations = new[]
        {
            GovernedLoopHumanInputWaitingCheckpointTestData.ConfigurationFor(HumanInputResponseKind.Text),
            GovernedLoopHumanInputWaitingCheckpointTestData.ConfigurationFor(HumanInputResponseKind.Choice),
            GovernedLoopHumanInputWaitingCheckpointTestData.ConfigurationFor(HumanInputResponseKind.Confirmation),
            GovernedLoopHumanInputWaitingCheckpointTestData.ConfigurationFor(HumanInputResponseKind.Structured),
            GovernedLoopHumanInputWaitingCheckpointTestData.ConfigurationFor(HumanInputResponseKind.Reference)
        };

        foreach (var configuration in configurations)
        {
            var checkpoint = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(configuration: configuration);
            var validation = GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint);
            var requestValidation = HumanInputValidator.ValidateRequest(checkpoint.Request);
            Assert.True(validation.IsValid, $"{configuration.ResponseSchema!.Kind}: {string.Join("; ", validation.Errors.Select(error => error.Code))}; request: {string.Join("; ", requestValidation.Errors.Select(error => error.Code))}");
            Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TrySerialize(checkpoint, out var json, out _));
            Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TryDeserialize(json, out var restarted, out _));
            Assert.Equal(checkpoint.CheckpointHash, restarted!.CheckpointHash);
        }
    }

    [Fact]
    public void Captured_nested_configuration_and_request_values_cannot_diverge_identity_or_serialization()
    {
        var choices = new[] { new HumanInputChoice("choice-one", "Choice one"), new HumanInputChoice("choice-two", "Choice two") };
        var fields = new[] { new HumanInputStructuredFieldSchema("field-one", HumanInputStructuredFieldKind.Choice, true, null, choices) };
        var configuration = new GovernedLoopHumanInputNodeConfiguration(1, "response-schema-one", "Collect one bounded preference.", "Choose one safe response.", new HumanInputResponseSchema(HumanInputResponseKind.Structured, null, null, fields, null), HumanInputPrivacyClass.Private, [new HumanInputEligibleRespondent("actor-one", "role-one", "route-one")], new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null), "timeout-policy-one@revision-one", "failure-policy-one@revision-one");
        var checkpoint = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(configuration: configuration);
        var hash = checkpoint.CheckpointHash;

        choices[0] = new HumanInputChoice("source-mutated", "Source mutated");
        checkpoint.NodeConfiguration.ResponseSchema!.StructuredFields![0].Choices![0] = new HumanInputChoice("returned-mutated", "Returned mutated");
        checkpoint.Request.ResponseSchema.StructuredFields![0].Choices![0] = new HumanInputChoice("returned-request-mutated", "Returned request mutated");

        Assert.Equal(hash, checkpoint.CheckpointHash);
        Assert.Equal("choice-one", checkpoint.NodeConfiguration.ResponseSchema!.StructuredFields![0].Choices![0].ChoiceId);
        Assert.Equal("choice-one", checkpoint.Request.ResponseSchema.StructuredFields![0].Choices![0].ChoiceId);
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractJson.TrySerialize(checkpoint, out var json, out _));
        Assert.Contains("choice-one", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mutated", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_secret_authority_approval_unknown_and_stale_or_divergent_artifacts_fail_closed()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var answered = GovernedLoopHumanInputWaitingCheckpointTestData.Answered(pending);
        var secret = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(configuration: GovernedLoopHumanInputWaitingCheckpointTestData.Configuration(prompt: "Paste api_key here."));
        var approval = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(configuration: GovernedLoopHumanInputWaitingCheckpointTestData.Configuration(timeoutPolicyReference: "approval-policy-one"));
        var unknown = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, pending.ResolvedPolicy, pending.Request, GovernedLoopHumanInputWaitingCheckpointPosture.Unknown, pending.Evidence, string.Empty));
        var stale = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(binding: GovernedLoopHumanInputWaitingCheckpointTestData.Binding(generation: 2));
        var divergent = GovernedLoopHumanInputWaitingCheckpointTestData.Pending(binding: GovernedLoopHumanInputWaitingCheckpointTestData.Binding(frontierHash: GovernedLoopHumanInputWaitingCheckpointTestData.Hash('a')));
        var authorityForged = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(answered.Evidence[1] with { AnswerSelection = answered.Evidence[1].AnswerSelection! with { SelectionId = "authority-grant-one" } });

        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(secret).IsValid);
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(approval).IsValid);
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(unknown).IsValid);
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(authorityForged).Errors, error => error.Code == "invalid_answer_selection");
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(pending, stale).Errors, error => error.Code == "immutable_coordinate_rebound");
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.DivergentReuse, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(pending, divergent));
    }

    [Fact]
    public void Self_hashed_semantically_invalid_checkpoint_and_evidence_artifacts_never_classify_as_replay()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var invalidCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, pending.Binding, pending.NodeConfiguration, pending.ResolvedPolicy, pending.Request, GovernedLoopHumanInputWaitingCheckpointPosture.Unknown, pending.Evidence, string.Empty));
        var invalidEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(pending.Evidence[0] with { TerminalizationReceiptId = "terminal-receipt-one", TerminalizationReceiptHash = GovernedLoopHumanInputWaitingCheckpointTestData.Hash('a') });

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(invalidCheckpoint));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(invalidCheckpoint).IsValid);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(invalidCheckpoint, pending));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(pending, invalidCheckpoint));
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(invalidEvidence));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(invalidEvidence).IsValid);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(invalidEvidence, pending.Evidence[0]));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(pending.Evidence[0], invalidEvidence));
    }

    [Fact]
    public void Self_hashed_default_utc_evidence_is_rejected_before_replay_classification()
    {
        var pending = GovernedLoopHumanInputWaitingCheckpointTestData.Pending();
        var defaultTimestampEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(pending.Evidence[0] with { OccurredAtUtc = default });

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(defaultTimestampEvidence));
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(defaultTimestampEvidence).Errors, error => error.Code == "timestamp_not_utc");
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(defaultTimestampEvidence, pending.Evidence[0]));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid, GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(pending.Evidence[0], defaultTimestampEvidence));
    }

    [Fact]
    public void Supersession_requires_a_distinct_replacing_checkpoint_identity()
    {
        var superseded = GovernedLoopHumanInputWaitingCheckpointTestData.Superseded();
        var selfSupersedingEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(superseded.Evidence[1] with { SupersedingCheckpointId = superseded.Binding.CheckpointId });
        var selfSuperseded = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, superseded.Binding, superseded.NodeConfiguration, superseded.ResolvedPolicy, superseded.Request, GovernedLoopHumanInputWaitingCheckpointPosture.Superseded, [superseded.Evidence[0], selfSupersedingEvidence], string.Empty));

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(superseded).IsValid);
        Assert.Contains(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(selfSuperseded).Errors, error => error.Code == "self_supersession");
    }

    [Fact]
    public void Evidence_hashes_bind_answer_supersession_and_terminalization_without_exposing_response_content_or_authority()
    {
        var answered = GovernedLoopHumanInputWaitingCheckpointTestData.Answered();
        var superseded = GovernedLoopHumanInputWaitingCheckpointTestData.Superseded();
        var terminal = GovernedLoopHumanInputWaitingCheckpointTestData.Terminal(answered);
        var forged = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(1, 2, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, answered.Evidence[1].OccurredAtUtc, answered.Evidence[1].AnswerSelection, "checkpoint-two", GovernedLoopHumanInputWaitingCheckpointTestData.Hash('j'), null, null, answered.Evidence[0].EvidenceHash, string.Empty));

        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(answered.Evidence[1]));
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(superseded.Evidence[1]));
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(terminal.Evidence[2]));
        Assert.False(GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(forged).IsValid);
    }
}
