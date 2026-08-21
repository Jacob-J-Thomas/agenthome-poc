using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using System.Text.Json;

namespace EmbodySense.Core.Common.Tests.Inference.Profiles;

public sealed class GovernedModelProfileContractTests
{
    private static readonly DateTimeOffset _canonicalNow = DateTimeOffset.Parse("2026-08-12T12:00:00Z");

    [Fact]
    public void Strict_profile_codec_round_trips_byte_identically()
    {
        var value = Metadata();
        Assert.True(GovernedModelContractJson.TrySerializeProfileMetadata(value, out var json, out var writeError), writeError);
        Assert.True(GovernedModelContractJson.TryDeserializeProfileMetadata(json, out var roundTrip, out var readError), readError);
        Assert.True(GovernedModelContractJson.TrySerializeProfileMetadata(roundTrip, out var encodedAgain, out _));

        Assert.NotNull(roundTrip);
        Assert.True(GovernedModelContractValidator.IsValid(roundTrip));
        Assert.Equal(json, encodedAgain);
    }

    [Fact]
    public void Deserialized_contracts_never_expose_mutable_collection_instances()
    {
        var metadata = Metadata();
        var policy = GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Inherit([Id("org.example/model-a")]), [Id("org.example/model-b")], Requirements());
        var admission = RoutingAdmission([Entry("node-a", "org.example/model-a", 'a')], _canonicalNow);

        Assert.True(GovernedModelContractJson.TrySerializeProfileMetadata(metadata, out var metadataJson, out _));
        Assert.True(GovernedModelContractJson.TryDeserializeProfileMetadata(metadataJson, out var readMetadata, out _));
        Assert.True(GovernedModelContractJson.TrySerializeRoutingPolicy(policy, out var policyJson, out _));
        Assert.True(GovernedModelContractJson.TryDeserializeRoutingPolicy(policyJson, out var readPolicy, out _));
        Assert.True(GovernedModelContractJson.TrySerializeRoutingAdmission(admission, out var admissionJson, out _));
        Assert.True(GovernedModelContractJson.TryDeserializeRoutingAdmission(admissionJson, out var readAdmission, out _));

        Assert.IsNotType<List<GovernedModelModality>>(readMetadata!.Modalities);
        Assert.IsNotType<List<GovernedModelCapability>>(readMetadata.Capabilities);
        Assert.IsNotType<List<string>>(readMetadata.PermittedRoleIds);
        Assert.IsNotType<List<string>>(readMetadata.Privacy.Regions);
        Assert.IsNotType<List<CapabilityId>>(readPolicy!.Selector.PermittedInheritedProfileIds);
        Assert.IsNotType<List<CapabilityId>>(readPolicy.FallbackProfileIds);
        Assert.IsNotType<List<GovernedModelRoutingAdmissionEntry>>(readAdmission!.Entries);
        Assert.IsNotType<List<CapabilityDataClass>>(readAdmission.Entries[0].AuthoredInputDataClasses);
        Assert.IsNotType<List<GovernedModelProfilePin>>(readAdmission.Entries[0].Fallbacks);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedModelModality>)readMetadata.Modalities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedModelRoutingAdmissionEntry>)readAdmission.Entries).Clear());
        Assert.True(GovernedModelContractValidator.IsValid(readMetadata));
        Assert.True(GovernedModelContractValidator.IsValid(readPolicy));
        Assert.True(GovernedModelContractValidator.IsValid(readAdmission));
    }

    [Fact]
    public void Aggregate_families_defensively_retain_every_source_collection()
    {
        var modalities = new[] { GovernedModelModality.Text };
        var capabilities = new[] { GovernedModelCapability.ToolCalling };
        var destinations = Array.Empty<string>();
        var classes = new[] { DataClass("public") };
        var regions = new[] { "us" };
        var privacy = GovernedModelPrivacyPosture.Create(1, GovernedModelLocality.LocalProcess, CapabilityEgressMode.None, destinations, classes, regions, GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);
        var requirements = GovernedModelProfileRequirements.Create(1, modalities, capabilities, 8_000, 512, PrivacyRequirement(true), Budget());
        var permitted = new[] { Id("org.example/model-a") };
        var selector = GovernedModelRoutingSelector.Inherit(permitted);
        var fallback = new[] { Id("org.example/model-b") };
        var policy = GovernedModelRoutingPolicy.Create(1, selector, fallback, requirements);
        var entries = new[] { Entry("node-a", "org.example/model-a", 'a') };
        var admission = RoutingAdmission(entries, _canonicalNow);

        modalities[0] = GovernedModelModality.Audio;
        capabilities[0] = GovernedModelCapability.Streaming;
        classes[0] = DataClass("restricted");
        regions[0] = "eu";
        permitted[0] = Id("org.example/model-c");
        fallback[0] = Id("org.example/model-c");
        entries[0] = Entry("node-z", "org.example/model-z", 'f');

        Assert.Equal(GovernedModelModality.Text, requirements.RequiredModalities[0]);
        Assert.Equal(GovernedModelCapability.ToolCalling, requirements.RequiredCapabilities[0]);
        Assert.Equal("public", privacy.AcceptedDataClasses[0].Value);
        Assert.Equal("us", privacy.Regions[0]);
        Assert.Equal("org.example/model-a", selector.PermittedInheritedProfileIds[0].Value);
        Assert.Equal("org.example/model-b", policy.FallbackProfileIds[0].Value);
        Assert.Equal("node-a", admission.Entries[0].NodeId);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("noncanonical-whitespace")]
    [InlineData("unknown-enum")]
    public void Strict_profile_codec_rejects_hostile_or_noncanonical_shapes(string mutation)
    {
        Assert.True(GovernedModelContractJson.TrySerializeProfileMetadata(Metadata(), out var canonical, out _));
        var json = mutation switch
        {
            "schema" => canonical!.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            "unknown-property" => canonical![..^1] + ",\"privateEndpoint\":\"https://secret\"}",
            "duplicate-property" => canonical![..^1] + ",\"providerId\":\"org.attacker\"}",
            "noncanonical-whitespace" => canonical + "\n",
            "unknown-enum" => canonical!.Replace("\"modalities\":[1]", "\"modalities\":[99]", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };

        Assert.False(GovernedModelContractJson.TryDeserializeProfileMetadata(json, out var value, out _));
        Assert.Null(value);
    }

    [Fact]
    public void Strict_usage_codec_rejects_null_nested_and_does_not_infer_from_text()
    {
        var usage = LlmInferenceUsageEvidence.Unavailable("codex-app-server", "v1");
        Assert.True(GovernedModelContractJson.TrySerializeUsageEvidence(usage, out var canonical, out _));
        var hostile = canonical!.Replace("\"inputTokens\":{", "\"inputTokensIgnored\":{", StringComparison.Ordinal);

        Assert.False(GovernedModelContractJson.TryDeserializeUsageEvidence(hostile, out _, out _));
        Assert.All([usage.InputTokens, usage.OutputTokens, usage.CachedTokens, usage.TotalTokens], value => Assert.Equal(GovernedModelUsageEvidenceStatus.Unavailable, value.Status));
        Assert.Equal(GovernedModelUsageEvidenceStatus.Unavailable, usage.MonetaryCost.Status);
    }

    [Fact]
    public void Strict_codec_rejects_oversized_input_before_parse()
    {
        Assert.False(GovernedModelContractJson.TryDeserializeUsageEvidence(new string('x', 300_000), out _, out var error));
        Assert.Equal("governed_model_contract_too_large", error);
    }

    [Fact]
    public void Strict_routing_codec_rejects_forged_nested_limit_shape()
    {
        var policy = GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(Id("org.example/model-a")), [], Requirements(Budget(output: GovernedModelUsageLimit.Bounded(100))));
        Assert.True(GovernedModelContractJson.TrySerializeRoutingPolicy(policy, out var canonical, out _));
        var hostile = canonical!.Replace("\"isBounded\":true,\"maximum\":100", "\"isBounded\":false,\"maximum\":100", StringComparison.Ordinal);

        Assert.NotEqual(canonical, hostile);
        Assert.False(GovernedModelContractJson.TryDeserializeRoutingPolicy(hostile, out _, out _));
    }

    [Fact]
    public void Strict_ledger_codec_rejects_forged_nested_identity_and_vector()
    {
        var identity = GovernedModelUsageLedgerIdentity.Create(1, "workspace-sha256:" + new string('1', 64), "run-default", "graph-default", "revision-1", new string('a', 64), 1, new string('d', 64), new string('e', 64), new string('f', 64), new string('0', 64), "node-inference", 0, 0, 1, "operation-attempt", 1, new string('b', 64), new string('c', 64));
        var reservation = GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Bounded(1), GovernedModelUsageLimit.Bounded(2), GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Bounded(3), GovernedModelMonetaryLimit.Bounded("USD", 4));
        var entry = GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, reservation, null, null, null, false, new string('d', 64), null, DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        Assert.True(GovernedModelContractJson.TrySerializeUsageLedgerEntry(entry, out var canonical, out var error), error);

        Assert.False(GovernedModelContractJson.TryDeserializeUsageLedgerEntry(canonical!.Replace("\"attemptNumber\":1", "\"attemptNumber\":0", StringComparison.Ordinal), out _, out _));
        Assert.False(GovernedModelContractJson.TryDeserializeUsageLedgerEntry(canonical.Replace("\"maximum\":1", "\"maximum\":-1", StringComparison.Ordinal), out _, out _));
    }

    [Fact]
    public void Strict_attempt_execution_evidence_codec_round_trips_and_rejects_incomplete_or_noncanonical_shape()
    {
        var evidence = GovernedModelAttemptExecutionEvidence.Create(
            1,
            Id("org.example/model-a"),
            new string('a', 64),
            new string('b', 64),
            "org.example",
            "codex-app-server",
            "gpt-5",
            LlmInferenceSurface.OpenAiCodex,
            new string('c', 64),
            new string('d', 64),
            GovernedModelUsageLedgerPhase.Reconciled,
            LlmInferenceUsageEvidence.Unavailable("codex-app-server", "v1"),
            true);

        Assert.True(GovernedModelContractJson.TrySerializeAttemptExecutionEvidence(evidence, out var canonical, out var writeError), writeError);
        Assert.True(GovernedModelContractJson.TryDeserializeAttemptExecutionEvidence(canonical, out var roundTrip, out var readError), readError);
        Assert.NotNull(roundTrip);
        Assert.Equal(evidence.ContentHash, roundTrip.ContentHash);
        Assert.True(GovernedModelContractJson.TrySerializeAttemptExecutionEvidence(roundTrip, out var encodedAgain, out _));
        Assert.Equal(canonical, encodedAgain);

        var missingTerminalHash = canonical!.Replace(",\"terminalUsageEntryHash\":\"" + new string('d', 64) + "\"", string.Empty, StringComparison.Ordinal);
        var nonTerminalPhase = canonical.Replace("\"terminalUsagePhase\":5", "\"terminalUsagePhase\":3", StringComparison.Ordinal);
        var invalidPin = canonical.Replace("\"profilePinHash\":\"" + new string('a', 64) + "\"", "\"profilePinHash\":\"short\"", StringComparison.Ordinal);
        var unknownProperty = canonical[..^1] + ",\"providerResponse\":\"secret\"}";

        Assert.False(GovernedModelContractJson.TryDeserializeAttemptExecutionEvidence(missingTerminalHash, out _, out _));
        Assert.False(GovernedModelContractJson.TryDeserializeAttemptExecutionEvidence(nonTerminalPhase, out _, out _));
        Assert.False(GovernedModelContractJson.TryDeserializeAttemptExecutionEvidence(invalidPin, out _, out _));
        Assert.False(GovernedModelContractJson.TryDeserializeAttemptExecutionEvidence(unknownProperty, out _, out _));
    }

    [Fact]
    public void Metadata_is_capability_backed_canonical_and_defensively_copied()
    {
        var modalities = new[] { GovernedModelModality.Text };
        var capabilities = new[] { GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming };
        var roles = new[] { "role/default" };
        var metadata = Metadata(modalities, capabilities, roles);

        modalities[0] = GovernedModelModality.Audio;
        capabilities[0] = GovernedModelCapability.StructuredOutput;
        roles[0] = "role/substituted";

        Assert.Equal(GovernedModelModality.Text, Assert.Single(metadata.Modalities));
        Assert.Equal([GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming], metadata.Capabilities);
        Assert.Equal("role/default", Assert.Single(metadata.PermittedRoleIds));
        Assert.Equal(64, metadata.ContentHash.Length);
    }

    [Fact]
    public void Profile_pin_requires_exact_model_profile_capability_identity()
    {
        var metadata = Metadata();
        var invalid = Pin(metadata, CapabilityKind.Actuator);

        Assert.Throws<ArgumentException>(() => ProfilePin(metadata, invalid));
        Assert.Equal(metadata.DescriptorIdentity.Id, ProfilePin(metadata).Capability.DescriptorIdentity.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Metadata_rejects_unknown_or_unsupported_modality(int value)
    {
        Assert.Throws<ArgumentException>(() => Metadata([(GovernedModelModality)value]));
    }

    [Fact]
    public void Metadata_rejects_noncanonical_or_duplicate_sets()
    {
        Assert.Throws<ArgumentException>(() => Metadata(capabilities: [GovernedModelCapability.Streaming, GovernedModelCapability.ToolCalling]));
        Assert.Throws<ArgumentException>(() => Metadata(capabilities: [GovernedModelCapability.ToolCalling, GovernedModelCapability.ToolCalling]));
        Assert.Throws<ArgumentException>(() => Metadata(roles: ["role/z", "role/a"]));
    }

    [Fact]
    public void Metadata_rejects_private_or_malformed_coordinates()
    {
        Assert.Throws<ArgumentException>(() => Metadata(providerId: "https://provider.example/token?secret=x"));
        Assert.Throws<ArgumentException>(() => Metadata(configurationHash: "sha256:" + new string('a', 64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metadata(configurationRevision: 0));
        Assert.Throws<ArgumentException>(() => GovernedModelProfileMetadata.Create(1, Metadata().DescriptorIdentity, "org.example", "codex", "gpt-5", "v1", 1, new string('a', 64), "unsafe\u202evalue", [GovernedModelModality.Text], [], 1, 1, Privacy(), GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable), [], []));
    }

    [Fact]
    public void Privacy_requires_closed_consistent_egress_shape()
    {
        Assert.Throws<ArgumentException>(() => GovernedModelPrivacyPosture.Create(1, GovernedModelLocality.Remote, CapabilityEgressMode.Restricted, [], [], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited));
        Assert.Throws<ArgumentException>(() => GovernedModelPrivacyPosture.Create(1, GovernedModelLocality.Remote, CapabilityEgressMode.None, ["api.example"], [], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedModelPrivacyPosture.Create(1, GovernedModelLocality.Unknown, CapabilityEgressMode.None, [], [], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited));
    }

    [Fact]
    public void Privacy_requirement_fails_closed_for_missing_or_wider_posture()
    {
        var requirement = PrivacyRequirement(localOnly: true);

        Assert.False(requirement.Satisfies(null, []));
        Assert.False(requirement.Satisfies(Privacy(locality: GovernedModelLocality.Remote), [DataClass("public")]));
        Assert.False(requirement.Satisfies(Privacy(), null));
        Assert.False(requirement.Satisfies(Privacy(), [DataClass("restricted")]));
        Assert.True(requirement.Satisfies(Privacy(), [DataClass("public")]));
    }

    [Fact]
    public void Usage_evidence_distinguishes_authoritative_zero_from_unavailable()
    {
        var unavailable = LlmInferenceUsageEvidence.Unavailable("codex-app-server", "v1");
        var authoritative = LlmInferenceUsageEvidence.Create(1, "codex-app-server", "v1", GovernedModelUsageMeasurement.Authoritative(0), GovernedModelUsageMeasurement.Authoritative(0), GovernedModelUsageMeasurement.Unavailable, GovernedModelUsageMeasurement.Authoritative(0), GovernedModelMonetaryUsageMeasurement.Authoritative("USD", 0));

        Assert.Equal(GovernedModelUsageEvidenceStatus.Unavailable, unavailable.InputTokens.Status);
        Assert.Equal(GovernedModelUsageEvidenceStatus.Authoritative, authoritative.InputTokens.Status);
        Assert.NotEqual(unavailable.ContentHash, authoritative.ContentHash);
    }

    [Fact]
    public void Usage_measurements_reject_overflow_and_invalid_currency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedModelUsageMeasurement.Authoritative(GovernedModelContractLimits.MaxTokens + 1));
        Assert.Throws<ArgumentException>(() => GovernedModelMonetaryUsageMeasurement.Authoritative("usd", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedModelMonetaryUsageMeasurement.Authoritative("USD", GovernedModelContractLimits.MaxCurrencyMicros + 1));
    }

    [Fact]
    public void Authoritative_cached_tokens_are_a_bounded_subset_of_authoritative_input()
    {
        var exactBoundary = LlmInferenceUsageEvidence.Create(
            1,
            "codex-app-server",
            "v1",
            GovernedModelUsageMeasurement.Authoritative(5),
            GovernedModelUsageMeasurement.Authoritative(2),
            GovernedModelUsageMeasurement.Authoritative(5),
            GovernedModelUsageMeasurement.Authoritative(7),
            GovernedModelMonetaryUsageMeasurement.Unavailable);
        var mixedUnavailable = LlmInferenceUsageEvidence.Create(
            1,
            "codex-app-server",
            "v1",
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Authoritative(6),
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelMonetaryUsageMeasurement.Unavailable);

        Assert.Equal(5, exactBoundary.CachedTokens.Value);
        Assert.Equal(6, mixedUnavailable.CachedTokens.Value);
        Assert.Throws<ArgumentException>(() => LlmInferenceUsageEvidence.Create(
            1,
            "codex-app-server",
            "v1",
            GovernedModelUsageMeasurement.Authoritative(5),
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Authoritative(6),
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelMonetaryUsageMeasurement.Unavailable));
    }

    [Fact]
    public void Hard_budget_rejects_post_hoc_reporting_only_support()
    {
        var budget = Budget(output: GovernedModelUsageLimit.Bounded(100));
        var reportingOnly = GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.AuthoritativeAfterDispatch, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable);
        var hardBounded = GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable);

        Assert.False(budget.CanBeHardEnforcedBy(reportingOnly));
        Assert.True(budget.CanBeHardEnforcedBy(hardBounded));
    }

    [Fact]
    public void Budget_rejects_inner_ceiling_that_widens_enclosing_ceiling()
    {
        var attempt = Ceiling(total: GovernedModelUsageLimit.Bounded(11));
        var node = Ceiling(total: GovernedModelUsageLimit.Bounded(10));
        var run = Ceiling(total: GovernedModelUsageLimit.Bounded(20));

        Assert.Throws<ArgumentException>(() => GovernedModelBudgetPolicy.Create(1, attempt, node, run));
    }

    [Fact]
    public void Budget_rejects_cross_currency_policy()
    {
        var attempt = Ceiling(cost: GovernedModelMonetaryLimit.Bounded("USD", 10));
        var node = Ceiling(cost: GovernedModelMonetaryLimit.Bounded("EUR", 20));

        Assert.Throws<ArgumentException>(() => GovernedModelBudgetPolicy.Create(1, attempt, node, Ceiling()));
    }

    [Fact]
    public void Usage_vector_preserves_zero_cost_currency_and_rejects_cost_without_currency()
    {
        Assert.Equal("USD", GovernedModelUsageVector.Create(0, 0, 0, 0, "USD", 0).Currency);
        Assert.Throws<ArgumentException>(() => GovernedModelUsageVector.Create(0, 0, 0, 0, null, 1));
        Assert.Equal(64, GovernedModelUsageVector.Create(1, 2, 3, 6, "USD", 7).ContentHash.Length);
    }

    [Fact]
    public void Inherit_is_explicitly_bounded_and_fails_closed_on_default_drift()
    {
        var first = Id("org.example/model-a");
        var second = Id("org.example/model-b");
        var selector = GovernedModelRoutingSelector.Inherit([first, second]);

        Assert.Equal(second, selector.Resolve(second));
        Assert.Null(selector.Resolve(Id("org.example/model-c")));
        Assert.Null(selector.Resolve(null));
    }

    [Fact]
    public void Inherit_rejects_empty_duplicate_and_noncanonical_permitted_sets()
    {
        var first = Id("org.example/model-a");
        var second = Id("org.example/model-b");

        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit([]));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit([first, first]));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit([second, first]));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit(Enumerable.Range(0, GovernedModelContractLimits.MaxSetValues + 1).Select(index => Id($"org.example/model-{index:d2}"))));
    }

    [Fact]
    public void Bounded_snapshot_rejects_lying_counts_and_stops_at_max_plus_one()
    {
        var values = Enumerable.Range(0, GovernedModelContractLimits.MaxSetValues + 1).Select(index => Id($"org.example/model-{index:d2}")).ToArray();
        var lyingLow = new HostileReadOnlyList<CapabilityId>(values, 0);
        var lyingHigh = new HostileReadOnlyList<CapabilityId>([values[0]], 1000);
        var underReported = new HostileReadOnlyList<CapabilityId>([values[0], values[1]], 1);
        var overReported = new HostileReadOnlyList<CapabilityId>([values[0]], 2);

        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit(lyingLow));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit(lyingHigh));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit(underReported));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingSelector.Inherit(overReported));
    }

    [Fact]
    public void Bounded_snapshot_propagates_throwing_enumerator_without_partial_value()
    {
        Assert.Throws<InvalidOperationException>(() => GovernedModelRoutingSelector.Inherit(ThrowingProfiles()));
    }

    [Fact]
    public void Routing_policy_preserves_fallback_order_but_rejects_duplicates_and_primary_overlap()
    {
        var primary = Id("org.example/model-a");
        var second = Id("org.example/model-b");
        var third = Id("org.example/model-c");
        var policy = GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(primary), [third, second], Requirements());

        Assert.Equal([primary, third, second], policy.ResolveCandidateOrder(null));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(primary), [second, second], Requirements()));
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(primary), [primary], Requirements()));
    }

    [Fact]
    public void Every_candidate_must_independently_satisfy_capability_privacy_and_hard_budget()
    {
        var requirements = Requirements(Budget(output: GovernedModelUsageLimit.Bounded(100)));
        var eligible = Metadata(usageSupport: GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable));
        var postHocOnly = Metadata(usageSupport: GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.AuthoritativeAfterDispatch, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable));

        Assert.True(requirements.SatisfiedBy(eligible, [DataClass("public")], "role/default", "inference"));
        Assert.False(requirements.SatisfiedBy(postHocOnly, [DataClass("public")], "role/default", "inference"));
        Assert.False(requirements.SatisfiedBy(eligible, [DataClass("restricted")], "role/default", "inference"));
    }

    [Fact]
    public void Admission_entry_is_primary_only_evidence_with_complete_ordered_fallbacks()
    {
        var primaryMetadata = Metadata();
        var fallbackMetadata = Metadata(profileId: "org.example/model-b", configurationHash: new string('b', 64));
        var primary = ProfilePin(primaryMetadata);
        var fallback = ProfilePin(fallbackMetadata);
        var source = new[] { fallback };
        var entry = GovernedModelRoutingAdmissionEntry.Create(1, "node-inference", "inference", new string('c', 64), Requirements(), true, [DataClass("public")], primary, source);
        source[0] = primary;

        Assert.Equal(fallback.ContentHash, Assert.Single(entry.Fallbacks).ContentHash);
        Assert.Equal(primary.ContentHash, entry.Primary.ContentHash);
        Assert.Throws<ArgumentException>(() => GovernedModelRoutingAdmissionEntry.Create(1, "node-inference", "inference", new string('c', 64), Requirements(), true, [DataClass("public")], primary, [primary]));
    }

    [Fact]
    public void Admission_snapshot_requires_UTC_canonical_node_order_and_defensive_copy()
    {
        var first = Entry("node-a", "org.example/model-a", 'a');
        var second = Entry("node-b", "org.example/model-b", 'b');
        var source = new[] { first, second };
        var snapshot = RoutingAdmission(source, DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        source[0] = second;

        Assert.Equal("node-a", snapshot.Entries[0].NodeId);
        Assert.Equal(64, snapshot.ContentHash.Length);
        Assert.Throws<ArgumentException>(() => RoutingAdmission([first], new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => RoutingAdmission([second, first], DateTimeOffset.Parse("2026-08-12T12:00:00Z")));
    }

    [Fact]
    public void Canonical_hashes_are_deterministic_and_domain_separated()
    {
        var first = Metadata();
        var second = Metadata();

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.ContentHash, first.Privacy.ContentHash);
        Assert.NotEqual(first.ContentHash, ProfilePin(first).ContentHash);
    }

    [Fact]
    public void Capability_pin_validator_rejects_one_field_forgery_matrix()
    {
        var metadata = Metadata();
        var valid = Pin(metadata);
        var forged = new CapabilityAdmissionPin?[]
        {
            null,
            valid with { Kind = CapabilityKind.Unknown },
            valid with { Implementation = new CapabilityImplementationIdentity(valid.Implementation.ProviderId, "Bad Path") },
            valid with { Provenance = valid.Provenance with { SourceUri = "https://artifacts.example.com/model?secret=x" } },
            valid with { Provenance = valid.Provenance with { Integrity = null } },
            valid with { Artifact = valid.Artifact with { Signature = "line\nbreak" } },
            valid with { SafeDescription = "unsafe\u202evalue" }
        };

        Assert.True(CapabilityAdmissionPinValidator.IsValid(valid));
        Assert.All(forged, value => Assert.False(CapabilityAdmissionPinValidator.IsValid(value)));
    }

    [Fact]
    public void Capability_pin_hash_binds_every_exact_semantic_field()
    {
        var metadata = Metadata();
        var valid = Pin(metadata);
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + new string('7', 64), out var digest, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('8', 64), out var descriptorHash, out _));
        var mutations = new CapabilityAdmissionPin[]
        {
            valid with { DescriptorIdentity = valid.DescriptorIdentity with { Id = Id("org.example/model-b") } },
            valid with { DescriptorIdentity = valid.DescriptorIdentity with { Version = CapabilityContractTestData.Version("2.0.0") } },
            valid with { DescriptorIdentity = valid.DescriptorIdentity with { Hash = descriptorHash! } },
            valid with { Kind = CapabilityKind.Skill },
            valid with { Implementation = valid.Implementation with { ProviderId = CapabilityContractTestData.Provider("org.other") } },
            valid with { Implementation = valid.Implementation with { ImplementationId = "model/other" } },
            valid with { Provenance = valid.Provenance with { SourceUri = "https://artifacts.example.com/model-v2" } },
            valid with { Provenance = valid.Provenance with { SourceRevision = "revision-2" } },
            valid with { Provenance = valid.Provenance with { Integrity = digest } },
            valid with { Artifact = valid.Artifact with { Checksum = digest } },
            valid with { Artifact = valid.Artifact with { Signature = "signature-v2" } },
            valid with { SafeDescription = "A different safe profile purpose." }
        };

        var expected = CapabilityAdmissionPinHash.Compute(valid);
        Assert.All(mutations, mutation => Assert.NotEqual(expected, CapabilityAdmissionPinHash.Compute(mutation)));
        Assert.NotEqual(expected, ProfilePin(metadata).ContentHash);
    }

    [Fact]
    public void Classified_input_data_must_be_bounded_canonical_and_duplicate_free()
    {
        var requirement = PrivacyRequirement(localOnly: true);
        var profile = Privacy();
        var repeated = Enumerable.Repeat(DataClass("public"), CapabilityContractLimits.MaxDataClasses + 1).ToArray();

        Assert.False(requirement.Satisfies(profile, repeated));
        Assert.False(requirement.Satisfies(profile, [DataClass("public"), DataClass("public")]));
    }

    private static GovernedModelRoutingAdmissionEntry Entry(string nodeId, string profileId, char hash)
    {
        var metadata = Metadata(profileId: profileId, configurationHash: new string(hash, 64));
        return GovernedModelRoutingAdmissionEntry.Create(1, nodeId, "inference", new string('c', 64), Requirements(), true, [DataClass("public")], ProfilePin(metadata), []);
    }

    private static GovernedModelProfilePin ProfilePin(GovernedModelProfileMetadata metadata, CapabilityAdmissionPin? capability = null)
        => GovernedModelProfilePin.Create(capability ?? Pin(metadata), metadata, new string('d', 64), new string('e', 64));

    private static GovernedModelRoutingAdmissionSnapshot RoutingAdmission(IEnumerable<GovernedModelRoutingAdmissionEntry> entries, DateTimeOffset evaluatedAtUtc)
        => GovernedModelRoutingAdmissionSnapshot.Create(
            1,
            "workspace-sha256:" + new string('1', 64),
            "operation-admit",
            new string('a', 64),
            new string('0', 64),
            "run-default",
            "graph-default",
            "revision-1",
            new string('b', 64),
            1,
            "role-default",
            1,
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            1,
            null,
            null,
            new string('f', 64),
            evaluatedAtUtc,
            entries);

    private static GovernedModelProfileRequirements Requirements(GovernedModelBudgetPolicy? budget = null)
        => GovernedModelProfileRequirements.Create(1, [GovernedModelModality.Text], [GovernedModelCapability.ToolCalling], 8_000, 512, PrivacyRequirement(localOnly: true), budget ?? Budget());

    private static GovernedModelPrivacyRequirement PrivacyRequirement(bool localOnly)
        => GovernedModelPrivacyRequirement.Create(1, localOnly, CapabilityEgressMode.None, [], [DataClass("public")], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);

    private static GovernedModelPrivacyPosture Privacy(GovernedModelLocality locality = GovernedModelLocality.LocalProcess)
        => GovernedModelPrivacyPosture.Create(1, locality, CapabilityEgressMode.None, [], [DataClass("public")], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);

    private static GovernedModelBudgetPolicy Budget(GovernedModelUsageLimit? output = null)
        => GovernedModelBudgetPolicy.Create(1, Ceiling(output: output), Ceiling(output: output), Ceiling(output: output));

    private static GovernedModelUsageCeiling Ceiling(GovernedModelUsageLimit? output = null, GovernedModelUsageLimit? total = null, GovernedModelMonetaryLimit? cost = null)
        => GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, output ?? GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, total ?? GovernedModelUsageLimit.Unbounded, cost ?? GovernedModelMonetaryLimit.Unbounded);

    private static GovernedModelProfileMetadata Metadata(
        GovernedModelModality[]? modalities = null,
        GovernedModelCapability[]? capabilities = null,
        string[]? roles = null,
        string providerId = "org.example",
        string profileId = "org.example/model-a",
        long configurationRevision = 1,
        string? configurationHash = null,
        GovernedModelUsageSupportPolicy? usageSupport = null)
    {
        var descriptor = Descriptor(profileId);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        return GovernedModelProfileMetadata.Create(
            1,
            identity!,
            providerId,
            "codex-app-server",
            "gpt-5",
            "v1",
            configurationRevision,
            configurationHash ?? new string('a', 64),
            "A test model profile.",
            modalities ?? [GovernedModelModality.Text],
            capabilities ?? [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
            128_000,
            8_192,
            Privacy(),
            usageSupport ?? GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable, GovernedModelUsageSupport.Unavailable),
            roles ?? ["role/default"],
            ["inference"]);
    }

    private static CapabilityAdmissionPin Pin(GovernedModelProfileMetadata metadata, CapabilityKind kind = CapabilityKind.ModelProfile)
    {
        var descriptor = Descriptor(metadata.DescriptorIdentity.Id.Value) with { Kind = kind };
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        return new CapabilityAdmissionPin(identity!, kind, descriptor.Implementation, descriptor.Provenance, new CapabilityDependencyArtifactMetadata(null, null), descriptor.Purpose);
    }

    private static CapabilityDescriptor Descriptor(string profileId)
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor();
        return descriptor with { Id = Id(profileId), Kind = CapabilityKind.ModelProfile, Purpose = "A test model profile." };
    }

    private static CapabilityId Id(string value) => CapabilityContractTestData.Id(value);
    private static CapabilityDataClass DataClass(string value) => CapabilityContractTestData.DataClass(value);

    private static IEnumerable<CapabilityId> ThrowingProfiles()
    {
        yield return Id("org.example/model-a");
        throw new InvalidOperationException("hostile enumeration");
    }
}
