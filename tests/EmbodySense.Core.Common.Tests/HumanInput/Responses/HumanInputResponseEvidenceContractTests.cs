using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Responses;

public sealed class HumanInputResponseEvidenceContractTests
{
    [Fact]
    public void Committed_pending_submit_and_withdraw_preserve_the_exact_request_head()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 2);
        var artifact = HumanInputResponseTestData.Artifact(request);
        var head = HumanInputResponseTestData.PendingHead(request);
        var submit = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, previousHead: head, resultHead: head);
        var withdraw = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, targets: [artifact], previousHead: head, resultHead: head);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(submit).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(withdraw).IsValid);
        Assert.Same(submit.PreviousHead, submit.ResultHead);
        Assert.Same(withdraw.PreviousHead, withdraw.ResultHead);
    }

    [Fact]
    public void Only_selection_producing_submit_advances_atomically_to_answered()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var previous = HumanInputResponseTestData.PendingHead(request);
        var answered = HumanInputResponseTestData.AnsweredHead(request, selection);
        var evidence = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, selection: selection, previousHead: previous, resultHead: answered);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, evidence.ResultHead!.Status);
        Assert.Equal(evidence.Selection, evidence.ResultHead.AnswerSelection);
        Assert.True(HumanInputRequestLifecycleValidator.ValidateHead(evidence.ResultHead).IsValid);
    }

    [Fact]
    public void Manual_select_targets_exactly_one_response_and_advances_atomically()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector"));
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact], selectorActorId: "selector-one", selectorRoleId: "role-selector");
        var previous = HumanInputResponseTestData.PendingHead(request);
        var answered = HumanInputResponseTestData.AnsweredHead(request, selection);
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Select,
            targets: [artifact],
            selection: selection,
            previousHead: previous,
            resultHead: answered,
            actorId: "selector-one",
            actorRoleId: "role-selector");

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid);
        Assert.Single(evidence.TargetResponses);
    }

    [Fact]
    public void Request_not_found_and_ineligible_outcomes_retain_authentication_without_inventing_role()
    {
        var request = HumanInputResponseTestData.Request();
        var requestNotFound = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.NotFound,
            HumanInputResponseOperationFailureCode.RequestNotFound,
            actorRoleId: null);
        var ineligible = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.IneligibleRespondent,
            actorRoleId: null);
        var committedWithoutRole = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: HumanInputResponseTestData.Artifact(request), actorRoleId: null);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(requestNotFound).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(ineligible).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(committedWithoutRole).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(requestNotFound with { ObservedBinding = request.Binding }).IsValid);
    }

    [Fact]
    public void Hostile_failure_evidence_cannot_invent_trusted_role_attribution()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var variants = new[]
        {
            HumanInputResponseTestData.Evidence(
                request,
                HumanInputResponseOperationKind.Submit,
                HumanInputResponseOperationOutcome.NotFound,
                HumanInputResponseOperationFailureCode.RequestNotFound,
                actorRoleId: "role-one"),
            HumanInputResponseTestData.Evidence(
                request,
                HumanInputResponseOperationKind.Submit,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.IneligibleRespondent,
                actorRoleId: "role-one"),
            HumanInputResponseTestData.Evidence(
                request,
                HumanInputResponseOperationKind.Select,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.IneligibleSelector,
                targets: [artifact],
                actorRoleId: "role-selector")
        };

        Assert.All(variants, evidence =>
        {
            var validation = HumanInputResponseContractValidator.ValidateEvidence(evidence);
            Assert.Contains(validation.Errors, error => error is { Code: HumanInputResponseValidationErrorCode.InvalidRole, Path: "$.actorRoleId" });
        });
    }

    [Fact]
    public void Observed_head_precedence_accepts_exact_optimistic_stale_and_terminal_evidence()
    {
        var request = HumanInputResponseTestData.Request();
        var optimisticHead = HumanInputResponseTestData.PendingHead(request) with { LifecycleVersion = 2, LastOperationId = "other-operation", UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(1) };
        var optimistic = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.OptimisticStateConflict,
            previousHead: optimisticHead,
            resultHead: optimisticHead,
            expectedLifecycleVersion: 1);

        var currentRequest = HumanInputRequestHash.Apply(request with { RequestVersionId = "request-version-two", Prompt = "Current prompt.", RequestHash = string.Empty });
        var staleHead = HumanInputResponseTestData.PendingHead(currentRequest) with { LifecycleVersion = 2, LastOperationId = "amend-one", UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(1) };
        var stale = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.StaleResponse,
            previousHead: staleHead,
            resultHead: staleHead,
            expectedLifecycleVersion: 1);

        var selected = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [selected]);
        var terminalHead = HumanInputResponseTestData.AnsweredHead(request, selection, "answer-one");
        var terminal = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.RequestTerminal,
            previousHead: terminalHead,
            resultHead: terminalHead,
            expectedLifecycleVersion: 1);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(optimistic).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(stale).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(terminal).IsValid);
    }

    [Fact]
    public void Observed_head_precedence_rejects_terminal_as_optimistic_and_terminal_stale_misclassification()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var terminalHead = HumanInputResponseTestData.AnsweredHead(request, selection, "answer-one");
        var terminalAsOptimistic = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.OptimisticStateConflict,
            previousHead: terminalHead,
            resultHead: terminalHead,
            expectedLifecycleVersion: 1);
        var terminalAsStale = terminalAsOptimistic with { FailureCode = HumanInputResponseOperationFailureCode.StaleResponse };
        var pendingSameVersionAsOptimistic = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.OptimisticStateConflict);

        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(terminalAsOptimistic).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(terminalAsStale).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(pendingSameVersionAsOptimistic).IsValid);
    }

    [Fact]
    public void Stale_and_terminal_evidence_reject_cross_lifecycle_heads()
    {
        var expected = HumanInputResponseTestData.Request();
        var other = HumanInputRequestHash.Apply(expected with
        {
            RequestId = "request-other",
            RequestVersionId = "request-other-version-one",
            RequestHash = string.Empty
        });
        var otherPending = HumanInputResponseTestData.PendingHead(other);
        var stale = HumanInputResponseTestData.Evidence(
            expected,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.StaleResponse,
            previousHead: otherPending,
            resultHead: otherPending,
            observedBinding: other.Binding);

        var otherArtifact = HumanInputResponseTestData.Artifact(other);
        var otherSelection = HumanInputResponseTestData.Selection(other, [otherArtifact]);
        var otherTerminal = HumanInputResponseTestData.AnsweredHead(other, otherSelection);
        var terminal = HumanInputResponseTestData.Evidence(
            expected,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.RequestTerminal,
            previousHead: otherTerminal,
            resultHead: otherTerminal,
            observedBinding: other.Binding);

        Assert.Contains(
            HumanInputResponseContractValidator.ValidateEvidence(stale).Errors,
            error => error.Path == "$.previousHead.requestId");
        Assert.Contains(
            HumanInputResponseContractValidator.ValidateEvidence(terminal).Errors,
            error => error.Path == "$.resultHead.requestId");
    }

    [Fact]
    public void Terminal_precedence_accepts_same_lifecycle_request_version_and_hash_drift()
    {
        var expected = HumanInputResponseTestData.Request();
        var current = HumanInputRequestHash.Apply(expected with
        {
            RequestVersionId = "request-version-two",
            Prompt = "Current terminal prompt.",
            RequestHash = string.Empty
        });
        var currentArtifact = HumanInputResponseTestData.Artifact(current);
        var currentSelection = HumanInputResponseTestData.Selection(current, [currentArtifact]);
        var currentTerminal = HumanInputResponseTestData.AnsweredHead(current, currentSelection);
        var evidence = HumanInputResponseTestData.Evidence(
            expected,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.RequestTerminal,
            previousHead: currentTerminal,
            resultHead: currentTerminal,
            observedBinding: current.Binding);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid);
    }

    [Fact]
    public void Evidence_time_accepts_equality_and_rejects_regression_for_unchanged_and_answered_heads()
    {
        var request = HumanInputResponseTestData.Request();
        var previous = HumanInputResponseTestData.PendingHead(request);
        var unchangedEqual = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.OperationIntentConflict,
            previousHead: previous,
            resultHead: previous,
            recordedAtUtc: previous.UpdatedAtUtc);
        var unchangedRegressed = unchangedEqual with { RecordedAtUtc = previous.UpdatedAtUtc.AddTicks(-1) };

        var artifact = HumanInputResponseTestData.Artifact(request);
        var equalSelection = HumanInputResponseTestData.Selection(request, [artifact], selectedAtUtc: previous.UpdatedAtUtc);
        var equalAnswered = HumanInputResponseTestData.AnsweredHead(request, equalSelection);
        var answeredEqual = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            submitted: artifact,
            selection: equalSelection,
            previousHead: previous,
            resultHead: equalAnswered,
            recordedAtUtc: previous.UpdatedAtUtc);

        var regressedSelection = HumanInputResponseTestData.Selection(
            request,
            [artifact],
            selectionId: "selection-regressed",
            selectedAtUtc: previous.UpdatedAtUtc.AddTicks(-1));
        var regressedAnswered = HumanInputResponseTestData.AnsweredHead(request, regressedSelection);
        var answeredRegressed = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            submitted: artifact,
            selection: regressedSelection,
            previousHead: previous,
            resultHead: regressedAnswered,
            recordedAtUtc: regressedSelection.SelectedAtUtc);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(unchangedEqual).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(answeredEqual).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(unchangedRegressed).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(answeredRegressed).IsValid);
    }

    [Theory]
    [InlineData(HumanInputResponseOperationKind.Withdraw)]
    [InlineData(HumanInputResponseOperationKind.Select)]
    public void Stale_request_reference_is_valid_for_response_targeting_operations(HumanInputResponseOperationKind kind)
    {
        var expected = HumanInputResponseTestData.Request();
        var target = HumanInputResponseTestData.Artifact(expected);
        var current = HumanInputRequestHash.Apply(expected with
        {
            RequestVersionId = "request-version-two",
            Prompt = "Current prompt.",
            RequestHash = string.Empty
        });
        var currentHead = HumanInputResponseTestData.PendingHead(current) with
        {
            LifecycleVersion = 2,
            LastOperationId = "amend-one",
            UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(1)
        };
        var evidence = HumanInputResponseTestData.Evidence(
            expected,
            kind,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.StaleResponse,
            targets: [target],
            previousHead: currentHead,
            resultHead: currentHead,
            expectedLifecycleVersion: 1,
            observedBinding: current.Binding);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid);
    }

    [Fact]
    public void Binding_only_staleness_is_proven_by_distinct_expected_and_observed_bindings()
    {
        var request = HumanInputResponseTestData.Request();
        var observed = request.Binding with { RunId = "run-two" };
        var stale = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.StaleResponse,
            observedBinding: observed);
        var fabricatedExact = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            submitted: HumanInputResponseTestData.Artifact(request),
            observedBinding: observed);
        var fabricatedOptimistic = stale with
        {
            FailureCode = HumanInputResponseOperationFailureCode.OptimisticStateConflict,
            ExpectedLifecycleVersion = 2
        };

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(stale).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(fabricatedExact).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(fabricatedOptimistic).IsValid);
    }

    [Fact]
    public void Late_response_classification_allows_submit_and_manual_select_but_not_withdraw()
    {
        var request = HumanInputResponseTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ImmutableArray.Create("role-selector"));
        var target = HumanInputResponseTestData.Artifact(request);
        var submit = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.LateResponse);
        var select = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Select,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.LateResponse,
            targets: [target],
            actorId: "selector-one",
            actorRoleId: "role-selector");
        var withdraw = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Withdraw,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.LateResponse,
            targets: [target]);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(submit).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(select).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(withdraw).IsValid);
    }

    [Fact]
    public void Lifecycle_version_exhaustion_allows_submit_and_select_but_rejects_withdraw()
    {
        var request = HumanInputResponseTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ImmutableArray.Create("role-selector"));
        var target = HumanInputResponseTestData.Artifact(request);
        var submit = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.LimitExceeded,
            HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded);
        var select = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Select,
            HumanInputResponseOperationOutcome.LimitExceeded,
            HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded,
            targets: [target],
            actorId: "selector-one",
            actorRoleId: "role-selector");
        var withdraw = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Withdraw,
            HumanInputResponseOperationOutcome.LimitExceeded,
            HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded,
            targets: [target]);

        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(submit).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(select).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(withdraw).IsValid);
    }

    [Theory]
    [InlineData(HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.MalformedResponse)]
    [InlineData(HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.DuplicateResponse)]
    [InlineData(HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.ResponseLimitExceeded)]
    [InlineData(HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded)]
    public void Inspected_submit_failures_require_the_exact_bounded_attempt(HumanInputResponseOperationOutcome outcome, HumanInputResponseOperationFailureCode failureCode)
    {
        var request = HumanInputResponseTestData.Request();
        var evidence = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, outcome, failureCode);

        Assert.NotNull(evidence.AttemptedResponse);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid);
        var missing = HumanInputResponseContractValidator.ValidateEvidence(evidence with { AttemptedResponse = null });
        Assert.Contains(missing.Errors, error => error is { Code: HumanInputResponseValidationErrorCode.InvalidEvidenceShape, Path: "$.attemptedResponse" });
        if (failureCode == HumanInputResponseOperationFailureCode.MalformedResponse)
        {
            Assert.False(HumanInputResponseContractValidator.ValidateArtifact(request, evidence.AttemptedResponse).IsValid);
        }
    }

    [Fact]
    public void Preinspection_committed_withdraw_and_select_dispositions_forbid_attempted_response_content()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector"));
        var response = HumanInputResponseTestData.Artifact(request);
        var optimisticHead = HumanInputResponseTestData.PendingHead(request) with
        {
            LifecycleVersion = 2,
            LastOperationId = "other-operation",
            UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(1)
        };
        var currentRequest = HumanInputRequestHash.Apply(request with { RequestVersionId = "request-version-two", Prompt = "Current prompt.", RequestHash = string.Empty });
        var staleHead = HumanInputResponseTestData.PendingHead(currentRequest) with
        {
            LifecycleVersion = 2,
            LastOperationId = "amend-one",
            UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(1)
        };
        var selected = HumanInputResponseTestData.Selection(request, [response], selectorActorId: "selector-one", selectorRoleId: "role-selector");
        var terminalHead = HumanInputResponseTestData.AnsweredHead(request, selected, "answer-one");
        var dispositions = new[]
        {
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: response),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.NotFound, HumanInputResponseOperationFailureCode.RequestNotFound, actorRoleId: null),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.RequestTerminal, previousHead: terminalHead, resultHead: terminalHead),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.StaleResponse, previousHead: staleHead, resultHead: staleHead, observedBinding: currentRequest.Binding),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OptimisticStateConflict, previousHead: optimisticHead, resultHead: optimisticHead, expectedLifecycleVersion: 1),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.IneligibleRespondent, actorRoleId: null),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.LateResponse),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OperationIntentConflict),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, targets: [response]),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Select, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded, targets: [response], actorId: "selector-one", actorRoleId: "role-selector")
        };

        Assert.All(dispositions, evidence =>
        {
            Assert.Null(evidence.AttemptedResponse);
            Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid, evidence.FailureCode.ToString());
            var attempt = HumanInputResponseTestData.Artifact(
                request,
                actorId: evidence.ActorId.Value,
                roleId: evidence.ActorRoleId ?? "role-one",
                submittedAtUtc: evidence.RecordedAtUtc);
            var validation = HumanInputResponseContractValidator.ValidateEvidence(evidence with { AttemptedResponse = attempt });
            Assert.Contains(validation.Errors, error => error.Path == "$.attemptedResponse");
        });
    }

    [Fact]
    public void Attempted_response_must_match_inspected_causality_but_not_request_relative_schema_or_privacy()
    {
        var request = HumanInputResponseTestData.Request();
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.DuplicateResponse);
        var attempt = evidence.AttemptedResponse!;
        Assert.True(AuthorityActorId.TryParse("user-two", out var otherActor, out _));
        var variants = new[]
        {
            HumanInputResponseArtifactHash.Apply(attempt with { Request = attempt.Request with { RequestVersionId = "request-version-two" }, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { Binding = attempt.Binding with { RunId = "run-two" }, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { ActorId = otherActor!, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { RespondentRoleId = "role-two", ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { SubmittedAtUtc = attempt.SubmittedAtUtc.AddTicks(1), ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { SchemaVersion = 2, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(attempt with { PrivacyClass = HumanInputPrivacyClass.Unknown, ResponseHash = string.Empty }),
            attempt with { ValueHash = HumanInputResponseTestData.Hash('d') },
            attempt with { ResponseHash = HumanInputResponseTestData.Hash('d') }
        };

        Assert.All(variants, variant => Assert.False(HumanInputResponseContractValidator.ValidateEvidence(evidence with { AttemptedResponse = variant }).IsValid));

        var schemaInvalid = HumanInputResponseArtifactHash.Apply(attempt with
        {
            Value = new HumanInputResponseValue(
                HumanInputResponseKind.Structured,
                null,
                null,
                null,
                ImmutableArray.Create(new HumanInputStructuredFieldValue("field-one", "private-value", null)),
                null),
            PrivacyClass = HumanInputPrivacyClass.Sensitive,
            ValueHash = string.Empty,
            ResponseHash = string.Empty
        });
        Assert.False(HumanInputResponseContractValidator.ValidateArtifact(request, schemaInvalid).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence with { AttemptedResponse = schemaInvalid }).IsValid);
    }

    [Fact]
    public void Evidence_snapshot_deep_copies_structured_attempts_and_accepts_equivalent_arrays()
    {
        var request = HumanInputResponseTestData.Request();
        var recordedAtUtc = HumanInputResponseTestData.Now.AddMinutes(5);
        var attempt = HumanInputResponseArtifactHash.Apply(HumanInputResponseTestData.Artifact(request, submittedAtUtc: recordedAtUtc) with
        {
            Value = new HumanInputResponseValue(
                HumanInputResponseKind.Structured,
                null,
                null,
                null,
                ImmutableArray.Create(new HumanInputStructuredFieldValue("field-one", "private-value", null)),
                null),
            ValueHash = string.Empty,
            ResponseHash = string.Empty
        });
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Rejected,
            HumanInputResponseOperationFailureCode.MalformedResponse,
            attempted: attempt,
            recordedAtUtc: recordedAtUtc);

        Assert.True(HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot?.AttemptedResponse);
        Assert.NotSame(evidence.AttemptedResponse, snapshot!.AttemptedResponse);
        Assert.NotSame(evidence.AttemptedResponse!.Value, snapshot.AttemptedResponse!.Value);
        Assert.False(evidence.AttemptedResponse.Value.StructuredFields!.Value.Equals(snapshot.AttemptedResponse.Value.StructuredFields!.Value));
        Assert.NotSame(evidence.AttemptedResponse.Value.StructuredFields.Value[0], snapshot.AttemptedResponse.Value.StructuredFields.Value[0]);

        var equivalent = HumanInputResponseArtifactHash.Apply(attempt with
        {
            Value = attempt.Value with
            {
                StructuredFields = attempt.Value.StructuredFields!.Value.Select(field => field with { }).ToImmutableArray()
            },
            ValueHash = string.Empty,
            ResponseHash = string.Empty
        });
        Assert.False(attempt.Value.StructuredFields!.Value.Equals(equivalent.Value.StructuredFields!.Value));
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence with { AttemptedResponse = equivalent }).IsValid);
        Assert.True(HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence with { AttemptedResponse = equivalent }, out _, out _));
    }

    [Fact]
    public void Eligibility_hash_rejects_hostile_answer_operation_substitutions_during_snapshot_capture()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var previous = HumanInputResponseTestData.PendingHead(request);
        var answered = HumanInputResponseTestData.AnsweredHead(request, selection);
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            submitted: artifact,
            selection: selection,
            previousHead: previous,
            resultHead: answered);
        Assert.True(AuthorityActorId.TryParse("user-two", out var otherActor, out _));
        var variants = new[]
        {
            evidence with { OperationId = "operation-two" },
            evidence with { CommandHash = HumanInputResponseTestData.Hash('d') },
            evidence with { Request = evidence.Request with { RequestVersionId = "request-version-two" } },
            evidence with { ExpectedBinding = evidence.ExpectedBinding with { WorkspaceId = "workspace-sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } },
            evidence with { ActorId = otherActor! },
            evidence with { ActorRoleId = "role-two" },
            evidence with { AuthenticationEvidenceHash = HumanInputResponseTestData.Hash('d') },
            evidence with { RecordedAtUtc = evidence.RecordedAtUtc.AddTicks(1) }
        };

        Assert.True(HumanInputResponseEligibilityEvidenceHash.Matches(evidence));
        Assert.True(HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out _, out _));
        Assert.All(variants, variant =>
        {
            Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(variant));
            Assert.False(HumanInputResponseOperationEvidenceSnapshot.TryCapture(variant, out _, out var validation));
            Assert.Contains(validation.Errors, error => error.Code == HumanInputResponseValidationErrorCode.InvalidEligibilityEvidence);
        });
    }

    [Fact]
    public void Eligibility_hash_fails_closed_on_noncanonical_inputs_and_malformed_evidence_hashes()
    {
        var request = HumanInputResponseTestData.Request();
        var reference = HumanInputResponseTestData.RequestReference(request);
        Assert.True(AuthorityActorId.TryParse("user-one", out var actor, out _));
        var commandHash = HumanInputResponseTestData.Hash('a');
        var authenticationHash = HumanInputResponseTestData.Hash('b');
        var hash = HumanInputResponseEligibilityEvidenceHash.Compute(
            request.Binding.WorkspaceId,
            "operation-one",
            commandHash,
            reference,
            actor!,
            "role-one",
            authenticationHash,
            HumanInputResponseTestData.Now);
        var invalidComputations = new Action[]
        {
            () => HumanInputResponseEligibilityEvidenceHash.Compute("Invalid", "operation-one", commandHash, reference, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "Invalid", commandHash, reference, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", "bad", reference, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, null!, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference with { RequestId = "Invalid" }, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, null!, "role-one", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, actor!, "Invalid", authenticationHash, HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, actor!, "role-one", "bad", HumanInputResponseTestData.Now),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, actor!, "role-one", authenticationHash, default),
            () => HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now.ToOffset(TimeSpan.FromHours(1)))
        };

        Assert.Equal(HumanInputLimits.Sha256HexCharacters, hash.Length);
        Assert.Equal(hash, HumanInputResponseEligibilityEvidenceHash.Compute(request.Binding.WorkspaceId, "operation-one", commandHash, reference, actor!, "role-one", authenticationHash, HumanInputResponseTestData.Now));
        Assert.All(invalidComputations, computation => Assert.Throws<ArgumentException>(computation));
        var evidence = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: HumanInputResponseTestData.Artifact(request));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(null));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with { EligibilityEvidenceHash = "bad" }));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with { EligibilityEvidenceHash = HumanInputResponseTestData.Hash('A') }));
    }

    [Fact]
    public void Evidence_snapshot_deep_copies_binding_head_and_target_reference_graphs()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var rawTargets = new[] { HumanInputResponseTestData.Reference(request, artifact) };
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Withdraw,
            targets: [artifact]) with
        {
            TargetResponses = ImmutableCollectionsMarshal.AsImmutableArray(rawTargets)
        };

        Assert.True(HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);
        Assert.NotSame(evidence.ExpectedBinding, snapshot.ExpectedBinding);
        Assert.NotSame(evidence.ObservedBinding, snapshot.ObservedBinding);
        Assert.NotSame(evidence.Request, snapshot.Request);
        Assert.NotSame(evidence.PreviousHead, snapshot.PreviousHead);
        Assert.NotSame(evidence.PreviousHead!.CurrentRequest, snapshot.PreviousHead!.CurrentRequest);
        Assert.NotSame(evidence.TargetResponses[0], snapshot.TargetResponses[0]);
        Assert.NotSame(evidence.TargetResponses[0].Request, snapshot.TargetResponses[0].Request);

        rawTargets[0] = rawTargets[0] with { ResponseId = "response-hostile" };

        Assert.Equal("response-one", snapshot.TargetResponses[0].ResponseId);
        Assert.False(HumanInputResponseOperationEvidenceSnapshot.TryCapture(null, out _, out var absent));
        Assert.False(absent.IsValid);
        Assert.False(HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence with { TargetResponses = default }, out _, out var malformed));
        Assert.False(malformed.IsValid);
    }

    [Fact]
    public void Every_failure_vocabulary_has_one_valid_value_free_evidence_shape()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var ordinary = new[]
        {
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OperationIntentConflict),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.DuplicateResponse),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.LateResponse),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.MalformedResponse),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.IneligibleRespondent, actorRoleId: null),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.ResponseLimitExceeded),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, HumanInputResponseOperationOutcome.NotFound, HumanInputResponseOperationFailureCode.ResponseNotFound, targets: [artifact]),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.ResponseAlreadyWithdrawn, targets: [artifact]),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Select, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.IneligibleSelector, targets: [artifact], actorRoleId: null),
            HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Select, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.SelectionConflict, targets: [artifact])
        };

        Assert.All(ordinary, evidence => Assert.True(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid, evidence.FailureCode.ToString()));
    }

    [Fact]
    public void Evidence_rejects_invalid_vocabulary_hashes_attribution_targets_and_operation_shapes()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var valid = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact);
        var tooManyTargets = Enumerable.Repeat(HumanInputResponseTestData.Reference(request, artifact), HumanInputResponseContractLimits.MaxSelectedResponses + 1).ToImmutableArray();
        var variants = new[]
        {
            valid with { SchemaVersion = 2 },
            valid with { OperationId = "Invalid" },
            valid with { CommandHash = "bad" },
            valid with { Kind = HumanInputResponseOperationKind.Unknown },
            valid with { Kind = (HumanInputResponseOperationKind)99 },
            valid with { Outcome = HumanInputResponseOperationOutcome.Unknown },
            valid with { FailureCode = HumanInputResponseOperationFailureCode.Unknown },
            valid with { Outcome = HumanInputResponseOperationOutcome.Conflict },
            valid with { FailureCode = HumanInputResponseOperationFailureCode.ResponseNotFound },
            valid with { Request = valid.Request with { RequestHash = "bad" } },
            valid with { ExpectedBinding = valid.ExpectedBinding with { CheckpointId = "Invalid" } },
            valid with { ObservedBinding = valid.ObservedBinding! with { CheckpointId = "Invalid" } },
            valid with { ObservedBinding = null },
            valid with { ExpectedLifecycleVersion = 0 },
            valid with { ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Answered },
            valid with { TargetResponses = default },
            valid with { TargetResponses = tooManyTargets },
            valid with { ActorId = null! },
            valid with { ActorRoleId = "Invalid" },
            valid with { AuthenticationEvidenceHash = "bad" },
            valid with { EligibilityEvidenceHash = "bad" },
            valid with { RecordedAtUtc = default }
        };

        Assert.All(variants, evidence => Assert.False(HumanInputResponseContractValidator.ValidateEvidence(evidence).IsValid));
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(null).IsValid);
    }

    [Fact]
    public void Evidence_rejects_head_fabrication_cross_request_targets_and_invalid_selection_coupling()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var head = HumanInputResponseTestData.PendingHead(request);
        var advanced = head with { LifecycleVersion = 2, LastOperationId = "operation-one", UpdatedAtUtc = HumanInputResponseTestData.Now.AddMinutes(5) };
        var invalidPendingAdvance = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, previousHead: head, resultHead: advanced);

        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var answered = HumanInputResponseTestData.AnsweredHead(request, selection);
        var answeredWithoutSelection = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, previousHead: head, resultHead: answered);
        var selectionWithoutAnswered = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, selection: selection, previousHead: head, resultHead: head);
        var withdrawWithSelection = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, targets: [artifact], selection: selection, previousHead: head, resultHead: answered);
        var selectMultiple = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Select, targets: [artifact, artifact], selection: selection, previousHead: head, resultHead: answered);
        var crossRequestTarget = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Withdraw, targets: [artifact]) with
        {
            TargetResponses = ImmutableArray.Create(HumanInputResponseTestData.Reference(request, artifact) with { Request = HumanInputResponseTestData.RequestReference(request) with { RequestVersionId = "version-other" } })
        };

        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(invalidPendingAdvance).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(answeredWithoutSelection).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(selectionWithoutAnswered).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(withdrawWithSelection).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(selectMultiple).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateEvidence(crossRequestTarget).IsValid);
    }

    [Fact]
    public void Answered_head_requires_one_exact_selection_reference_and_nonanswered_heads_forbid_it()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var answered = HumanInputResponseTestData.AnsweredHead(request, selection);
        var pendingWithSelection = HumanInputResponseTestData.PendingHead(request) with { AnswerSelection = answered.AnswerSelection };
        var answeredWithoutSelection = answered with { AnswerSelection = null };
        var crossRequest = answered with { AnswerSelection = answered.AnswerSelection! with { Request = answered.AnswerSelection.Request with { RequestVersionId = "version-other" } } };
        var malformed = answered with { AnswerSelection = answered.AnswerSelection! with { SelectionHash = "bad" } };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateHead(answered).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(pendingWithSelection).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(answeredWithoutSelection).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(crossRequest).IsValid);
        Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(malformed).IsValid);
    }

    [Fact]
    public void Evidence_default_formatting_omits_actor_role_binding_and_private_artifact_content()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request, text: "value-canary", explanation: "explanation-canary");
        var evidence = HumanInputResponseTestData.Evidence(request, HumanInputResponseOperationKind.Submit, submitted: artifact, actorId: "user-one", actorRoleId: "role-one");
        var text = evidence.ToString();

        Assert.DoesNotContain("value-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("explanation-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user-one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role-one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_default_formatting_redacts_attempted_content_attribution_and_authority_hashes()
    {
        var request = HumanInputResponseTestData.Request();
        var recordedAtUtc = HumanInputResponseTestData.Now.AddMinutes(5);
        var attempt = HumanInputResponseTestData.Artifact(
            request,
            actorId: "actor-canary",
            roleId: "role-canary",
            text: "attempt-value-canary",
            submittedAtUtc: recordedAtUtc,
            explanation: "attempt-explanation-canary");
        var evidence = HumanInputResponseTestData.Evidence(
            request,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Conflict,
            HumanInputResponseOperationFailureCode.DuplicateResponse,
            attempted: attempt,
            actorId: "actor-canary",
            actorRoleId: "role-canary",
            recordedAtUtc: recordedAtUtc);
        var text = evidence.ToString();

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt-value-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt-explanation-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actor-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.AuthenticationEvidenceHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.EligibilityEvidenceHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.AttemptedResponse!.ValueHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.AttemptedResponse.ResponseHash, text, StringComparison.Ordinal);
    }
}
