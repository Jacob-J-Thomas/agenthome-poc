using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

internal static class HumanInputRequestStoreTestData
{
    internal const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    internal static readonly DateTimeOffset Time = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static HumanInputRequestLifecycleStoreMutation CreateMutation(
        string requestId = "request-one",
        string requestVersionId = "version-one",
        string operationId = "create-one",
        string requestHash = HashA,
        long generation = 0,
        string prompt = "Private prompt one.")
    {
        var request = Request(requestId, requestVersionId, Time, prompt: prompt);
        var head = Head(request, 1, HumanInputRequestLifecycleStatus.Pending, 0, null, null, operationId, Time);
        var evidence = Evidence(
            HumanInputRequestLifecycleOperationKind.Create,
            requestId,
            operationId,
            requestHash,
            Time,
            null,
            head,
            request);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, request, head, null);
    }

    internal static HumanInputRequestLifecycleStoreMutation TransitionMutation(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest previousRequest,
        HumanInputRequestLifecycleHead previousHead,
        long generation,
        string operationId,
        string requestHash)
    {
        var recordedAt = kind == HumanInputRequestLifecycleOperationKind.Expire
            ? previousRequest.Timing.ExpiresAtUtc.AddTicks(1)
            : previousHead.UpdatedAtUtc.AddMinutes(1);
        HumanInputRequest? candidate = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Reroute => Rehash(previousRequest with
            {
                RequestVersionId = "version-rerouted",
                EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")]
            }),
            HumanInputRequestLifecycleOperationKind.Amend => Rehash(previousRequest with
            {
                RequestVersionId = "version-amended",
                Prompt = "Private amended prompt."
            }),
            _ => null
        };
        var status = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Reject => HumanInputRequestLifecycleStatus.Rejected,
            HumanInputRequestLifecycleOperationKind.Cancel => HumanInputRequestLifecycleStatus.Cancelled,
            HumanInputRequestLifecycleOperationKind.Expire => HumanInputRequestLifecycleStatus.Expired,
            _ => HumanInputRequestLifecycleStatus.Pending
        };
        var resultHead = Head(
            candidate ?? previousRequest,
            previousHead.LifecycleVersion + 1,
            status,
            kind == HumanInputRequestLifecycleOperationKind.Remind
                ? previousHead.ReminderCount + 1
                : previousHead.ReminderCount,
            previousHead.SupersedesRequestId,
            previousHead.SupersededByRequestId,
            operationId,
            recordedAt);
        var evidence = Evidence(
            kind,
            previousRequest.RequestId,
            operationId,
            requestHash,
            recordedAt,
            previousHead,
            resultHead,
            candidate);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, candidate, resultHead, null);
    }

    internal static HumanInputRequestLifecycleStoreMutation SupersedeMutation(
        HumanInputRequest previousRequest,
        HumanInputRequestLifecycleHead previousHead,
        long generation,
        string operationId = "supersede-one",
        string requestHash = HashB)
    {
        var recordedAt = previousHead.UpdatedAtUtc.AddMinutes(1);
        var candidate = Request(
            "request-two",
            "version-two",
            recordedAt,
            previousRequest.Binding,
            HumanInputPrivacyClass.Sensitive,
            "Private replacement prompt.");
        var primary = Head(
            previousRequest,
            previousHead.LifecycleVersion + 1,
            HumanInputRequestLifecycleStatus.Superseded,
            previousHead.ReminderCount,
            previousHead.SupersedesRequestId,
            candidate.RequestId,
            operationId,
            recordedAt);
        var secondary = Head(
            candidate,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            0,
            previousRequest.RequestId,
            null,
            operationId,
            recordedAt);
        var evidence = Evidence(
            HumanInputRequestLifecycleOperationKind.Supersede,
            previousRequest.RequestId,
            operationId,
            requestHash,
            recordedAt,
            previousHead,
            primary,
            candidate,
            candidate.RequestId,
            null,
            secondary);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, candidate, primary, secondary);
    }

    internal static HumanInputRequestLifecycleStoreMutation ReceiptMutation(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failureCode,
        HumanInputRequestLifecycleHead? previousHead,
        long generation,
        string operationId,
        string requestHash)
    {
        var target = previousHead?.RequestId ?? "request-missing";
        var evidence = Evidence(
            kind,
            target,
            operationId,
            requestHash,
            (previousHead?.UpdatedAtUtc ?? Time).AddMinutes(1),
            previousHead,
            previousHead,
            null,
            outcome: outcome,
            failureCode: failureCode);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, null, null, null);
    }

    internal static HumanInputRequest Request(
        string requestId,
        string requestVersionId,
        DateTimeOffset requestedAt,
        HumanInputRequestBinding? binding = null,
        HumanInputPrivacyClass privacy = HumanInputPrivacyClass.Private,
        string prompt = "Private prompt one.")
    {
        var request = new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            requestId,
            requestVersionId,
            binding ?? new HumanInputRequestBinding("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "governed-loop", "loop-revision-one", "node-one", "run-one", "checkpoint-one"),
            "Collect one bounded datum.",
            prompt,
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null),
            privacy,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputTiming(requestedAt, requestedAt.AddHours(1)),
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, "node-one", "checkpoint-one"),
            string.Empty);
        return Rehash(request);
    }

    internal static HumanInputRequestLifecycleHead Head(
        HumanInputRequest request,
        long lifecycleVersion,
        HumanInputRequestLifecycleStatus status,
        int reminderCount,
        string? supersedesRequestId,
        string? supersededByRequestId,
        string operationId,
        DateTimeOffset updatedAt)
        => new(
            1,
            request.RequestId,
            lifecycleVersion,
            status,
            Reference(request),
            reminderCount,
            supersedesRequestId,
            supersededByRequestId,
            operationId,
            updatedAt);

    internal static HumanInputRequestLifecycleOperationEvidence Evidence(
        HumanInputRequestLifecycleOperationKind kind,
        string targetRequestId,
        string operationId,
        string requestHash,
        DateTimeOffset recordedAt,
        HumanInputRequestLifecycleHead? previousHead,
        HumanInputRequestLifecycleHead? resultHead,
        HumanInputRequest? candidate,
        string? relatedRequestId = null,
        HumanInputRequestLifecycleHead? relatedPreviousHead = null,
        HumanInputRequestLifecycleHead? relatedResultHead = null,
        HumanInputRequestLifecycleOperationOutcome outcome = HumanInputRequestLifecycleOperationOutcome.Committed,
        HumanInputRequestLifecycleOperationFailureCode failureCode = HumanInputRequestLifecycleOperationFailureCode.None)
    {
        var requiresGrant = kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;
        return new HumanInputRequestLifecycleOperationEvidence(
            1,
            operationId,
            requestHash,
            kind,
            outcome,
            failureCode,
            targetRequestId,
            kind == HumanInputRequestLifecycleOperationKind.Create ? 0 : previousHead?.LifecycleVersion ?? 1,
            kind == HumanInputRequestLifecycleOperationKind.Create ? HumanInputRequestLifecycleStatus.Unknown : previousHead?.Status ?? HumanInputRequestLifecycleStatus.Pending,
            kind == HumanInputRequestLifecycleOperationKind.Create ? null : previousHead?.CurrentRequest,
            kind == HumanInputRequestLifecycleOperationKind.Create
                ? null
                : candidate?.Binding ?? new HumanInputRequestBinding("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "governed-loop", "loop-revision-one", "node-one", "run-one", "checkpoint-one"),
            previousHead,
            resultHead,
            relatedRequestId,
            relatedPreviousHead,
            relatedResultHead,
            candidate is null ? null : Reference(candidate),
            Actor(),
            Reason(),
            requiresGrant ? Grant() : null,
            HashB,
            requiresGrant ? HashC : null,
            recordedAt);
    }

    internal static HumanInputRequestReference Reference(HumanInputRequest request)
    {
        if (!HumanInputRequestReference.TryCreate(request, out var reference, out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        return reference!;
    }

    internal static HumanInputRequest Rehash(HumanInputRequest request)
        => HumanInputRequestHash.Apply(request with { RequestHash = string.Empty });

    private static AuthorityActorId Actor()
    {
        _ = AuthorityActorId.TryParse("user-owner", out var actor, out _);
        return actor!;
    }

    private static AuthorityPurpose Reason()
    {
        _ = AuthorityPurpose.TryParse("Manage one exact bounded Human Input request.", out var reason, out _);
        return reason!;
    }

    private static AuthorityGrantReference Grant()
    {
        _ = AuthorityGrantId.TryParse("grant-one", out var grantId, out _);
        _ = AuthorityGrantRevision.TryParse("1", out var revision, out _);
        return new AuthorityGrantReference(grantId!, revision!, "sha256:" + HashA);
    }
}
