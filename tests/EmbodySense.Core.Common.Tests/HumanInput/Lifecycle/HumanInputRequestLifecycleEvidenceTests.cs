using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleEvidenceTests
{
    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire)]
    public void Closed_committed_operation_shapes_validate(HumanInputRequestLifecycleOperationKind kind)
    {
        var current = HumanInputLifecycleTestData.Request();
        var previous = kind == HumanInputRequestLifecycleOperationKind.Create ? null : HumanInputLifecycleTestData.Head(current);
        var candidate = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Create => current,
            HumanInputRequestLifecycleOperationKind.Reroute => HumanInputLifecycleTestData.Rerouted(current),
            HumanInputRequestLifecycleOperationKind.Amend => HumanInputLifecycleTestData.Amended(current),
            _ => null
        };
        var resultRequest = candidate ?? current;
        var result = kind == HumanInputRequestLifecycleOperationKind.Create
            ? HumanInputLifecycleTestData.Head(resultRequest, operationId: "operation-two", updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1))
            : HumanInputLifecycleTestData.Head(
                resultRequest,
                lifecycleVersion: 2,
                status: Status(kind),
                reminderCount: kind == HumanInputRequestLifecycleOperationKind.Remind ? 1 : 0,
                operationId: "operation-two",
                updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        var evidence = HumanInputLifecycleTestData.Evidence(kind, previous, result, candidate);

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
    }

    [Fact]
    public void Create_evidence_requires_a_canonically_absent_optimistic_expectation()
    {
        var candidate = HumanInputLifecycleTestData.Request();
        var result = HumanInputLifecycleTestData.Head(
            candidate,
            operationId: "operation-two",
            updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        var valid = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Create,
            null,
            result,
            candidate);
        var variants = new[]
        {
            valid with { ExpectedLifecycleVersion = 1 },
            valid with { ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Pending },
            valid with { ExpectedRequest = HumanInputLifecycleTestData.Reference(candidate) },
            valid with { ExpectedBinding = candidate.Binding },
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(valid).IsValid);
        Assert.All(
            variants,
            variant => Assert.Contains(
                HumanInputRequestLifecycleValidator.ValidateEvidence(variant).Errors,
                error => error.Code == HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape));
    }

    [Fact]
    public void Noncreate_evidence_requires_a_complete_bounded_expected_state_and_composite_binding()
    {
        var request = HumanInputLifecycleTestData.Request();
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Cancelled,
            LastOperationId = "operation-two",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1),
        };
        var valid = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Cancel,
            previous,
            result);
        var malformedBindings = new[]
        {
            request.Binding with { WorkspaceId = "Invalid" },
            request.Binding with { LoopGraphId = "Invalid" },
            request.Binding with { LoopRevisionId = "Invalid" },
            request.Binding with { NodeId = "Invalid" },
            request.Binding with { RunId = "Invalid" },
            request.Binding with { CheckpointId = "Invalid" },
        };
        var variants = new List<HumanInputRequestLifecycleOperationEvidence>
        {
            valid with { ExpectedLifecycleVersion = 0 },
            valid with { ExpectedLifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion + 1 },
            valid with { ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Unknown },
            valid with { ExpectedRequest = null },
            valid with { ExpectedRequest = valid.ExpectedRequest! with { RequestId = "request-other" } },
            valid with { ExpectedRequest = valid.ExpectedRequest! with { RequestVersionId = "Invalid" } },
            valid with { ExpectedBinding = null },
        };
        variants.AddRange(malformedBindings.Select(binding => valid with { ExpectedBinding = binding }));

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(valid).IsValid);
        Assert.All(variants, variant => Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(variant).IsValid));
    }

    [Fact]
    public void Committed_transition_binds_expected_generation_request_version_and_graph_to_the_previous_artifact()
    {
        var request = HumanInputLifecycleTestData.Request();
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Cancelled,
            LastOperationId = "operation-two",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1),
        };
        var valid = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Cancel,
            previous,
            result,
            expectedArtifact: request);
        var otherVersion = HumanInputLifecycleTestData.Request(requestVersionId: "version-other");
        var variants = new[]
        {
            valid with { ExpectedLifecycleVersion = valid.ExpectedLifecycleVersion + 1 },
            valid with { ExpectedRequest = HumanInputLifecycleTestData.Reference(otherVersion) },
            valid with { ExpectedBinding = request.Binding with { LoopGraphId = "governed-loop-other" } },
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateCommittedTransition(valid, request, null).IsValid);
        foreach (var variant in variants)
        {
            Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(variant).IsValid);
            Assert.Contains(
                HumanInputRequestLifecycleValidator.ValidateCommittedTransition(variant, request, null).Errors,
                error => error.Code == HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);
        }
    }

    [Fact]
    public void Not_found_evidence_retains_the_full_expected_state_without_forging_an_observed_head()
    {
        var expectedArtifact = HumanInputLifecycleTestData.Request();
        var evidence = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Cancel,
            null,
            null,
            outcome: HumanInputRequestLifecycleOperationOutcome.NotFound,
            failureCode: HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound,
            expectedArtifact: expectedArtifact);

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
        Assert.Equal(1, evidence.ExpectedLifecycleVersion);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, evidence.ExpectedLifecycleStatus);
        Assert.Equal(HumanInputLifecycleTestData.Reference(expectedArtifact), evidence.ExpectedRequest);
        Assert.Equal(expectedArtifact.Binding, evidence.ExpectedBinding);
        Assert.Null(evidence.PreviousHead);
        Assert.Null(evidence.ResultHead);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.Committed, HumanInputRequestLifecycleOperationFailureCode.None)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded)]
    public void Supported_operation_outcome_failure_pairs_validate(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure)
    {
        var evidence = outcome == HumanInputRequestLifecycleOperationOutcome.Committed
            ? CommittedCancelEvidence()
            : FailureEvidence(kind, outcome, failure);

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
    }

    [Fact]
    public void Operation_failure_matrix_accepts_only_semantically_possible_classifications()
    {
        foreach (var kind in SupportedKinds())
        {
            foreach (var (outcome, failure) in FailurePairs())
            {
                var evidence = FailureEvidence(kind, outcome, failure);
                var expected = IsSupportedFailure(kind, failure);

                Assert.Equal(expected, HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
            }
        }
    }

    [Fact]
    public void Evidence_rejects_operation_failure_combinations_that_cannot_occur()
    {
        var cancelCandidateConflict = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Cancel,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        var createNotFound = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationOutcome.NotFound,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound);
        var createOptimisticConflict = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        var createCandidateConflict = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        var changedIntent = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Amend,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OperationIntentConflict);

        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(cancelCandidateConflict).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(createNotFound).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(createOptimisticConflict).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(createCandidateConflict).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(changedIntent).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend, HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded)]
    public void Noncommitted_candidate_must_bind_the_exact_target_or_supersede_related_request(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure)
    {
        var evidence = FailureEvidence(kind, outcome, failure);
        var substituted = evidence with
        {
            CandidateRequest = evidence.CandidateRequest! with { RequestId = "request-substituted" }
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(substituted).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound, false)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists, true)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict, false)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded, false)]
    public void Failure_target_observation_shape_is_operation_specific(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure,
        bool requiresObservedTarget)
    {
        var evidence = FailureEvidence(kind, outcome, failure);
        var observedHead = HumanInputLifecycleTestData.Head(HumanInputLifecycleTestData.Request());
        var contradictory = evidence with
        {
            PreviousHead = requiresObservedTarget ? null : observedHead,
            ResultHead = requiresObservedTarget ? null : observedHead
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(contradictory).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict)]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound)]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded)]
    public void Noncommitted_supersede_allows_a_conclusive_absent_related_observation(
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure)
    {
        var evidence = FailureEvidence(HumanInputRequestLifecycleOperationKind.Supersede, outcome, failure);

        Assert.Null(evidence.RelatedPreviousHead);
        Assert.Null(evidence.RelatedResultHead);
        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict, true, false)]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound, false, true)]
    [InlineData(HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded, true, true)]
    public void Noncommitted_supersede_rejects_inconclusive_or_changed_related_observations(
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure,
        bool includePrevious,
        bool includeResult)
    {
        var evidence = FailureEvidence(HumanInputRequestLifecycleOperationKind.Supersede, outcome, failure);
        var relatedRequest = HumanInputLifecycleTestData.Request("request-two", "version-existing");
        var relatedPrevious = HumanInputLifecycleTestData.Head(relatedRequest);
        var relatedResult = relatedPrevious with
        {
            LifecycleVersion = 2,
            LastOperationId = "operation-three",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(2)
        };
        var changed = evidence with
        {
            RelatedPreviousHead = includePrevious ? relatedPrevious : null,
            RelatedResultHead = includeResult ? relatedResult : null
        };

        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(changed).IsValid);
    }

    [Fact]
    public void Noncommitted_supersede_accepts_an_exact_no_change_related_observation()
    {
        var evidence = FailureEvidence(
            HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        var relatedRequest = HumanInputLifecycleTestData.Request("request-two", "version-existing");
        var relatedHead = HumanInputLifecycleTestData.Head(relatedRequest);
        var observed = evidence with { RelatedPreviousHead = relatedHead, RelatedResultHead = relatedHead };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(observed).IsValid);
    }

    [Fact]
    public void Evidence_rejects_changed_vocabularies_hashes_heads_candidates_grants_and_related_shapes()
    {
        var request = HumanInputLifecycleTestData.Request();
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Cancelled,
            LastOperationId = "operation-two",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1)
        };
        var valid = HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Cancel, previous, result);
        var variants = new[]
        {
            valid with { SchemaVersion = 2 },
            valid with { OperationId = "Invalid" },
            valid with { RequestHash = new string('a', 63) },
            valid with { Kind = HumanInputRequestLifecycleOperationKind.Unknown },
            valid with { Outcome = HumanInputRequestLifecycleOperationOutcome.Unknown },
            valid with { FailureCode = HumanInputRequestLifecycleOperationFailureCode.Unknown },
            valid with { FailureCode = HumanInputRequestLifecycleOperationFailureCode.None, Outcome = HumanInputRequestLifecycleOperationOutcome.Conflict },
            valid with { TargetRequestId = "request-other" },
            valid with { CandidateRequest = HumanInputLifecycleTestData.Reference(request) },
            valid with { RelatedRequestId = "request-two" },
            valid with { AuthorityEvidenceHash = new string('A', 64) },
            valid with { GrantDependencyEvidenceHash = HumanInputLifecycleTestData.Hash('c') },
            valid with { RecordedAtUtc = default }
        };

        Assert.All(variants, variant => Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(variant).IsValid));
        Assert.False(HumanInputRequestLifecycleValidator.ValidateEvidence(null).IsValid);
    }

    [Fact]
    public void Evidence_text_is_value_free_and_omits_actor_reason_grant_and_heads()
    {
        var request = HumanInputLifecycleTestData.Request(prompt: "prompt-canary", respondents: [new("user-one", "role-one", "route-canary")]);
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with { LifecycleVersion = 2, LastOperationId = "operation-two", UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1) };
        var evidence = HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Remind, previous, result);

        var text = evidence.ToString();

        Assert.DoesNotContain("prompt-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("route-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Manage one exact", text, StringComparison.Ordinal);
        Assert.DoesNotContain("grant-one", text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedRequest!.RequestVersionId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding!.WorkspaceId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding.LoopGraphId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding.LoopRevisionId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding.NodeId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding.RunId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ExpectedBinding.CheckpointId, text, StringComparison.Ordinal);
        Assert.Contains(evidence.OperationId, text, StringComparison.Ordinal);
    }

    private static HumanInputRequestLifecycleStatus Status(HumanInputRequestLifecycleOperationKind kind) => kind switch
    {
        HumanInputRequestLifecycleOperationKind.Reject => HumanInputRequestLifecycleStatus.Rejected,
        HumanInputRequestLifecycleOperationKind.Cancel => HumanInputRequestLifecycleStatus.Cancelled,
        HumanInputRequestLifecycleOperationKind.Expire => HumanInputRequestLifecycleStatus.Expired,
        _ => HumanInputRequestLifecycleStatus.Pending
    };

    private static HumanInputRequestLifecycleOperationEvidence CommittedCancelEvidence()
    {
        var request = HumanInputLifecycleTestData.Request();
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Cancelled,
            LastOperationId = "operation-two",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1)
        };
        return HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Cancel, previous, result);
    }

    private static HumanInputRequestLifecycleOperationEvidence FailureEvidence(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failure)
    {
        var request = HumanInputLifecycleTestData.Request();
        var head = HumanInputLifecycleTestData.Head(request);
        var targetAbsent = outcome == HumanInputRequestLifecycleOperationOutcome.NotFound
            || kind == HumanInputRequestLifecycleOperationKind.Create
                && failure != HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists;
        var candidate = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Create => request,
            HumanInputRequestLifecycleOperationKind.Reroute => HumanInputLifecycleTestData.Rerouted(request),
            HumanInputRequestLifecycleOperationKind.Amend => HumanInputLifecycleTestData.Amended(request),
            HumanInputRequestLifecycleOperationKind.Supersede => HumanInputLifecycleTestData.Request(
                "request-two",
                binding: request.Binding,
                requestedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1),
                expiresAtUtc: HumanInputLifecycleTestData.Now.AddHours(1)),
            _ => null
        };

        return HumanInputLifecycleTestData.Evidence(
            kind,
            targetAbsent ? null : head,
            targetAbsent ? null : head,
            candidate,
            outcome,
            failure,
            relatedRequestId: kind == HumanInputRequestLifecycleOperationKind.Supersede ? candidate!.RequestId : null,
            expectedArtifact: request);
    }

    private static IEnumerable<HumanInputRequestLifecycleOperationKind> SupportedKinds()
        => Enum.GetValues<HumanInputRequestLifecycleOperationKind>()
            .Where(kind => kind != HumanInputRequestLifecycleOperationKind.Unknown);

    private static IEnumerable<(HumanInputRequestLifecycleOperationOutcome Outcome, HumanInputRequestLifecycleOperationFailureCode Failure)> FailurePairs()
    {
        yield return (HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        yield return (HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists);
        yield return (HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal);
        yield return (HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        yield return (HumanInputRequestLifecycleOperationOutcome.Conflict, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        yield return (HumanInputRequestLifecycleOperationOutcome.NotFound, HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound);
        yield return (HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded);
        yield return (HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded);
        yield return (HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded);
        yield return (HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded);
        yield return (HumanInputRequestLifecycleOperationOutcome.LimitExceeded, HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded);
    }

    private static bool IsSupportedFailure(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationFailureCode failure)
    {
        return failure switch
        {
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict => kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound => kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists => kind == HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal => kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict => kind is HumanInputRequestLifecycleOperationKind.Reroute
                or HumanInputRequestLifecycleOperationKind.Amend
                or HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict => true,
            HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded => kind is HumanInputRequestLifecycleOperationKind.Create
                or HumanInputRequestLifecycleOperationKind.Reroute
                or HumanInputRequestLifecycleOperationKind.Amend
                or HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded => kind == HumanInputRequestLifecycleOperationKind.Remind,
            HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded => true,
            HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded => kind is HumanInputRequestLifecycleOperationKind.Create
                or HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded => kind != HumanInputRequestLifecycleOperationKind.Create,
            _ => false
        };
    }
}
