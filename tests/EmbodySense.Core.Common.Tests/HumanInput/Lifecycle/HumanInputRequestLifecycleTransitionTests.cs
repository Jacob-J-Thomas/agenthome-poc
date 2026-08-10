using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleTransitionTests
{
    [Fact]
    public void Every_non_response_lifecycle_transition_has_one_valid_closed_shape()
    {
        var current = HumanInputLifecycleTestData.Request();

        AssertValid(Create());
        AssertValid(Remind(current));
        AssertValid(Reroute(current));
        AssertValid(Amend(current));
        AssertValid(Terminal(current, HumanInputRequestLifecycleOperationKind.Reject, HumanInputRequestLifecycleStatus.Rejected));
        AssertValid(Terminal(current, HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleStatus.Cancelled));
        AssertValid(Expire(current));
        AssertValid(Supersede(current));
    }

    [Fact]
    public void Response_deadline_is_inclusive_and_expiry_is_strictly_after_it()
    {
        var request = HumanInputLifecycleTestData.Request(expiresAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        var atEndpoint = Remind(request, HumanInputLifecycleTestData.Now.AddMinutes(1));
        var expireAtEndpoint = Expire(request, HumanInputLifecycleTestData.Now.AddMinutes(1));
        var expireAfterEndpoint = Expire(request, HumanInputLifecycleTestData.Now.AddMinutes(1).AddTicks(1));

        AssertValid(atEndpoint);
        AssertInvalid(expireAtEndpoint, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict);
        AssertValid(expireAfterEndpoint);
    }

    [Fact]
    public void Reroute_changes_only_exact_routing_on_a_new_request_version()
    {
        var previous = HumanInputLifecycleTestData.Request();
        AssertValid(Reroute(previous));

        var noChange = HumanInputLifecycleTestData.Rehash(previous with { RequestVersionId = "version-two" });
        AssertInvalid(Reroute(previous, noChange), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);

        var promptChange = HumanInputLifecycleTestData.Rehash(HumanInputLifecycleTestData.Rerouted(previous) with { Prompt = "Changed prompt." });
        AssertInvalid(Reroute(previous, promptChange), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);

        var bindingChange = HumanInputLifecycleTestData.Rehash(HumanInputLifecycleTestData.Rerouted(previous) with
        {
            Binding = previous.Binding with { RunId = "run-two" }
        });
        AssertInvalid(Reroute(previous, bindingChange), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);
    }

    [Fact]
    public void Amend_changes_only_admitted_fields_and_never_weakens_privacy()
    {
        var previous = HumanInputLifecycleTestData.Request();
        AssertValid(Amend(previous));

        var stronger = HumanInputLifecycleTestData.Rehash(previous with
        {
            RequestVersionId = "version-two",
            PrivacyClass = HumanInputPrivacyClass.Sensitive
        });
        AssertValid(Amend(previous, stronger));

        var sensitive = HumanInputLifecycleTestData.Request(privacy: HumanInputPrivacyClass.Sensitive);
        var downgrade = HumanInputLifecycleTestData.Rehash(sensitive with
        {
            RequestVersionId = "version-two",
            PrivacyClass = HumanInputPrivacyClass.Private,
            Prompt = "Changed prompt."
        });
        AssertInvalid(Amend(sensitive, downgrade), HumanInputRequestLifecycleValidationErrorCode.PrivacyDowngrade);

        var rerouted = HumanInputLifecycleTestData.Rehash(previous with
        {
            RequestVersionId = "version-two",
            Prompt = "Changed prompt.",
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")]
        });
        AssertInvalid(Amend(previous, rerouted), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);

        var changedBinding = HumanInputLifecycleTestData.Rehash(previous with
        {
            RequestVersionId = "version-two",
            Prompt = "Changed prompt.",
            Binding = previous.Binding with { CheckpointId = "checkpoint-two" },
            ContinuationBinding = previous.ContinuationBinding with { CheckpointId = "checkpoint-two" }
        });
        AssertInvalid(Amend(previous, changedBinding), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);
    }

    [Fact]
    public void Candidate_operations_require_new_version_identity_and_hash()
    {
        var previous = HumanInputLifecycleTestData.Request();
        var sameVersion = HumanInputLifecycleTestData.Rehash(previous with
        {
            Prompt = "Changed prompt while reusing the version."
        });
        var sameHash = HumanInputLifecycleTestData.Amended(previous) with { RequestHash = previous.RequestHash };

        AssertInvalid(Amend(previous, sameVersion), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);
        AssertInvalid(Amend(previous) with { CandidateRequest = sameHash }, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference);
    }

    [Fact]
    public void Supersede_is_one_atomic_two_head_transition_with_exact_binding_and_lineage()
    {
        var previous = HumanInputLifecycleTestData.Request();
        AssertValid(Supersede(previous));

        var sameId = HumanInputLifecycleTestData.Request(
            requestVersionId: "version-two",
            requestedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1),
            expiresAtUtc: HumanInputLifecycleTestData.Now.AddHours(1));
        AssertInvalid(Supersede(previous, sameId), HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession);

        var otherBinding = HumanInputLifecycleTestData.Request(
            requestId: "request-two",
            requestVersionId: "version-two",
            binding: previous.Binding with { LoopRevisionId = "loop-revision-two" },
            requestedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1),
            expiresAtUtc: HumanInputLifecycleTestData.Now.AddHours(1));
        AssertInvalid(Supersede(previous, otherBinding), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);

        var valid = Supersede(previous);
        AssertInvalid(
            valid with { Evidence = valid.Evidence with { RelatedPreviousHead = valid.Evidence.RelatedResultHead } },
            HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession);
    }

    [Fact]
    public void Terminal_stale_overflow_clock_rollback_and_post_expiry_transitions_fail_closed()
    {
        var request = HumanInputLifecycleTestData.Request(expiresAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(2));
        var reminder = Remind(request);
        var terminalPrevious = reminder.Evidence.PreviousHead! with { Status = HumanInputRequestLifecycleStatus.Cancelled };
        AssertInvalid(reminder with { Evidence = reminder.Evidence with { PreviousHead = terminalPrevious } }, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        var overflowPrevious = reminder.Evidence.PreviousHead! with { LifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion };
        var overflowResult = reminder.Evidence.ResultHead! with { LifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion };
        AssertInvalid(reminder with { Evidence = reminder.Evidence with { PreviousHead = overflowPrevious, ResultHead = overflowResult } }, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        var rolledBack = reminder with
        {
            Evidence = reminder.Evidence with
            {
                RecordedAtUtc = HumanInputLifecycleTestData.Now.AddSeconds(-1),
                ResultHead = reminder.Evidence.ResultHead! with { UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddSeconds(-1) }
            }
        };
        AssertInvalid(rolledBack, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        var afterExpiry = Remind(request, request.Timing.ExpiresAtUtc.AddTicks(1));
        AssertInvalid(afterExpiry, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict);
    }

    [Fact]
    public void Committed_transition_validation_fails_closed_for_invalid_invocation_and_successor_shapes()
    {
        var request = HumanInputLifecycleTestData.Request();
        var head = HumanInputLifecycleTestData.Head(request);
        var conflict = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Cancel,
            head,
            head,
            outcome: HumanInputRequestLifecycleOperationOutcome.Conflict,
            failureCode: HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);

        Assert.False(HumanInputRequestLifecycleValidator.ValidateCommittedTransition(null, null, null).IsValid);
        Assert.Contains(
            HumanInputRequestLifecycleValidator.ValidateCommittedTransition(conflict, request, null).Errors,
            error => error.Code == HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        var remind = Remind(request);
        AssertInvalid(remind with { PreviousRequest = request with { RequestHash = HumanInputLifecycleTestData.Hash('f') } }, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference);
        AssertInvalid(remind with { PreviousRequest = HumanInputLifecycleTestData.Request(requestVersionId: "version-other") }, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference);
        AssertInvalid(remind with { CandidateRequest = request }, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);
        AssertInvalid(
            remind with { Evidence = remind.Evidence with { ResultHead = remind.Evidence.ResultHead! with { ReminderCount = 0 } } },
            HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        AssertInvalid(Create() with { PreviousRequest = request }, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);
        var create = Create();
        AssertInvalid(
            create with { Evidence = create.Evidence with { RecordedAtUtc = create.Evidence.RecordedAtUtc.AddTicks(1) } },
            HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);

        var cancel = Terminal(request, HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleStatus.Cancelled);
        AssertInvalid(
            cancel with { Evidence = cancel.Evidence with { ResultHead = cancel.Evidence.ResultHead! with { ReminderCount = 1 } } },
            HumanInputRequestLifecycleValidationErrorCode.InvalidTransition);
    }

    [Fact]
    public void Structured_schema_comparison_is_deep_and_operation_sensitive()
    {
        var previous = HumanInputLifecycleTestData.StructuredRequest();
        AssertValid(Amend(previous));

        var changedField = HumanInputLifecycleTestData.Rerouted(previous) with
        {
            ResponseSchema = previous.ResponseSchema with
            {
                StructuredFields =
                [
                    previous.ResponseSchema.StructuredFields![0] with { FieldId = "field-changed" },
                    previous.ResponseSchema.StructuredFields[1]
                ]
            }
        };
        changedField = HumanInputLifecycleTestData.Rehash(changedField);

        AssertInvalid(Reroute(previous, changedField), HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation);
    }

    private static TransitionCase Create()
    {
        var recorded = HumanInputLifecycleTestData.Now.AddMinutes(1);
        var candidate = HumanInputLifecycleTestData.Request(requestedAtUtc: recorded, expiresAtUtc: recorded.AddHours(1));
        var result = HumanInputLifecycleTestData.Head(candidate, operationId: "operation-two", updatedAtUtc: recorded);
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Create, null, result, candidate, recordedAtUtc: recorded), null, candidate);
    }

    private static TransitionCase Remind(HumanInputRequest previous, DateTimeOffset? recordedAtUtc = null)
    {
        var recorded = recordedAtUtc ?? HumanInputLifecycleTestData.Now.AddMinutes(1);
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = previousHead with
        {
            LifecycleVersion = 2,
            ReminderCount = 1,
            LastOperationId = "operation-two",
            UpdatedAtUtc = recorded
        };
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Remind,
            previousHead,
            result,
            recordedAtUtc: recorded,
            expectedArtifact: previous), previous, null);
    }

    private static TransitionCase Reroute(HumanInputRequest previous, HumanInputRequest? candidate = null)
    {
        candidate ??= HumanInputLifecycleTestData.Rerouted(previous);
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = HumanInputLifecycleTestData.Head(candidate, lifecycleVersion: 2, operationId: "operation-two", updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Reroute,
            previousHead,
            result,
            candidate,
            expectedArtifact: previous), previous, candidate);
    }

    private static TransitionCase Amend(HumanInputRequest previous, HumanInputRequest? candidate = null)
    {
        candidate ??= HumanInputLifecycleTestData.Amended(previous);
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = HumanInputLifecycleTestData.Head(candidate, lifecycleVersion: 2, operationId: "operation-two", updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Amend,
            previousHead,
            result,
            candidate,
            expectedArtifact: previous), previous, candidate);
    }

    private static TransitionCase Terminal(HumanInputRequest previous, HumanInputRequestLifecycleOperationKind kind, HumanInputRequestLifecycleStatus status)
    {
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = previousHead with
        {
            LifecycleVersion = 2,
            Status = status,
            LastOperationId = "operation-two",
            UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1)
        };
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(
            kind,
            previousHead,
            result,
            expectedArtifact: previous), previous, null);
    }

    private static TransitionCase Expire(HumanInputRequest previous, DateTimeOffset? recordedAtUtc = null)
    {
        var recorded = recordedAtUtc ?? previous.Timing.ExpiresAtUtc.AddTicks(1);
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = previousHead with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Expired,
            LastOperationId = "operation-two",
            UpdatedAtUtc = recorded
        };
        return new TransitionCase(HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Expire,
            previousHead,
            result,
            recordedAtUtc: recorded,
            expectedArtifact: previous), previous, null);
    }

    private static TransitionCase Supersede(HumanInputRequest previous, HumanInputRequest? candidate = null)
    {
        var recorded = HumanInputLifecycleTestData.Now.AddMinutes(1);
        candidate ??= HumanInputLifecycleTestData.Request(
            requestId: "request-two",
            requestVersionId: "version-two",
            requestedAtUtc: recorded,
            expiresAtUtc: recorded.AddHours(1));
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var result = previousHead with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Superseded,
            SupersededByRequestId = candidate.RequestId,
            LastOperationId = "operation-two",
            UpdatedAtUtc = recorded
        };
        var relatedResult = HumanInputLifecycleTestData.Head(candidate, supersedesRequestId: previous.RequestId, operationId: "operation-two", updatedAtUtc: recorded);
        var evidence = HumanInputLifecycleTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Supersede,
            previousHead,
            result,
            candidate,
            targetRequestId: previous.RequestId,
            relatedRequestId: candidate.RequestId,
            relatedResultHead: relatedResult,
            recordedAtUtc: recorded,
            expectedArtifact: previous);
        return new TransitionCase(evidence, previous, candidate);
    }

    private static void AssertValid(TransitionCase testCase)
    {
        var result = HumanInputRequestLifecycleValidator.ValidateCommittedTransition(testCase.Evidence, testCase.PreviousRequest, testCase.CandidateRequest);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(error => $"{error.Code}: {error.Path}: {error.Message}")));
    }

    private static void AssertInvalid(TransitionCase testCase, HumanInputRequestLifecycleValidationErrorCode expected)
    {
        var result = HumanInputRequestLifecycleValidator.ValidateCommittedTransition(testCase.Evidence, testCase.PreviousRequest, testCase.CandidateRequest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == expected);
    }

    private sealed record TransitionCase(HumanInputRequestLifecycleOperationEvidence Evidence, HumanInputRequest? PreviousRequest, HumanInputRequest? CandidateRequest);
}
