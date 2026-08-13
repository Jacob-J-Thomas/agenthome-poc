using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanInputResponseCrossProcessHost
{
    private const string AuthenticationEvidenceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AuthorizationEvidenceHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAsync(
        string mode,
        string workspaceRoot,
        string trustRoot,
        string releaseMarker,
        string readyMarker,
        string resultMarker,
        string operationId,
        string responseId,
        string actorId,
        string actorRoleId,
        string boundaryText)
    {
        var options = CreateOptions(mode, boundaryText);
        var store = new HumanInputRequestStore(
            new WorkspacePaths(workspaceRoot),
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        var read = await store.ReadAsync("request-one");
        var snapshot = read.PrimarySnapshot ?? throw new InvalidOperationException("The cross-process Human Input request was not available.");
        var request = snapshot.RequestVersions.Single(snapshot.Head.CurrentRequest.Matches);
        var mutation = CreateSubmitMutation(
            request,
            snapshot.Head,
            expectedStoreGeneration: 1,
            operationId,
            responseId,
            answer: string.Equals(mode, "crash", StringComparison.Ordinal),
            actorId,
            actorRoleId);

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        var result = await ((IHumanInputResponseLifecycleStore)store).CommitAsync(mutation);
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, result.Status.ToString());
        return 0;
    }

    private static HumanInputRequestStoreOptions? CreateOptions(string mode, string boundaryText)
    {
        if (string.Equals(mode, "writer", StringComparison.Ordinal))
        {
            return null;
        }
        if (!string.Equals(mode, "crash", StringComparison.Ordinal)
            || !Enum.TryParse<HumanInputRequestPersistenceBoundary>(boundaryText, ignoreCase: false, out var boundary))
        {
            throw new ArgumentException("The Human Input response child-host mode or crash boundary is invalid.", nameof(mode));
        }

        return new HumanInputRequestStoreOptions
        {
            DurableBoundaryObserver = (observed, _) =>
            {
                if (observed == boundary)
                {
                    CrossProcessMarkerProtocol.TerminateAbruptly();
                }

                return ValueTask.CompletedTask;
            }
        };
    }

    private static HumanInputResponseLifecycleStoreMutation CreateSubmitMutation(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        long expectedStoreGeneration,
        string operationId,
        string responseId,
        bool answer,
        string actorId,
        string actorRoleId)
    {
        var recordedAtUtc = _createdAtUtc.AddMinutes(expectedStoreGeneration);
        var actor = ParseActor(actorId);
        var requestReference = CreateRequestReference(request);
        var artifact = HumanInputResponseArtifactHash.Apply(new HumanInputResponseArtifact(
            HumanInputResponseArtifact.CurrentSchemaVersion,
            responseId,
            requestReference,
            request.Binding,
            actor,
            actorRoleId,
            recordedAtUtc,
            request.PrivacyClass,
            new HumanInputResponseValue(HumanInputResponseKind.Text, "Private response data.", null, null, null, null),
            "Private explanation.",
            string.Empty,
            string.Empty));
        if (!HumanInputResponseReference.TryCreate(request, artifact, out var responseReference, out var responseValidation))
        {
            throw new InvalidOperationException(string.Join(',', responseValidation.Errors));
        }

        HumanInputResponseSelection? selection = null;
        HumanInputResponseSelectionReference? selectionReference = null;
        var resultHead = head;
        if (answer)
        {
            selection = HumanInputResponseSelectionHash.Apply(new HumanInputResponseSelection(
                HumanInputResponseSelection.CurrentSchemaVersion,
                operationId,
                requestReference,
                request.ResponsePolicy.Kind,
                ImmutableArray.Create(responseReference!),
                null,
                null,
                recordedAtUtc,
                string.Empty));
            selectionReference = HumanInputResponseSelectionReference.Create(selection);
            resultHead = head with
            {
                LifecycleVersion = head.LifecycleVersion + 1,
                Status = HumanInputRequestLifecycleStatus.Answered,
                LastOperationId = operationId,
                UpdatedAtUtc = recordedAtUtc,
                AnswerSelection = selectionReference
            };
        }

        var commandHash = HumanInputResponseLifecycleCommandHash.Apply(new HumanInputResponseLifecycleCommand(
            HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
            operationId,
            HumanInputResponseOperationKind.Submit,
            request.RequestId,
            head.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending,
            requestReference,
            request.Binding,
            artifact.ResponseId,
            artifact.Value,
            artifact.Explanation,
            [],
            string.Empty)).CommandHash;
        var evidence = Authenticate(new HumanInputResponseOperationEvidence(
            HumanInputResponseOperationEvidence.CurrentSchemaVersion,
            operationId,
            commandHash,
            HumanInputResponseOperationKind.Submit,
            HumanInputResponseOperationOutcome.Committed,
            HumanInputResponseOperationFailureCode.None,
            requestReference,
            request.Binding,
            request.Binding,
            head.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending,
            head,
            resultHead,
            null,
            responseReference,
            [],
            selectionReference,
            actor,
            actorRoleId,
            AuthenticationEvidenceHash,
            AuthorizationEvidenceHash,
            recordedAtUtc));
        return new HumanInputResponseLifecycleStoreMutation(expectedStoreGeneration, evidence, artifact, selection, answer ? resultHead : null);
    }

    private static HumanInputResponseOperationEvidence Authenticate(HumanInputResponseOperationEvidence evidence)
        => evidence with
        {
            EligibilityEvidenceHash = HumanInputResponseEligibilityEvidenceHash.Compute(
                evidence.ExpectedBinding.WorkspaceId,
                evidence.OperationId,
                evidence.CommandHash,
                evidence.Request,
                evidence.ActorId,
                evidence.ActorRoleId,
                evidence.AuthenticationEvidenceHash,
                evidence.RecordedAtUtc)
        };

    private static HumanInputRequestReference CreateRequestReference(HumanInputRequest request)
    {
        if (!HumanInputRequestReference.TryCreate(request, out var reference, out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors));
        }

        return reference!;
    }

    private static AuthorityActorId ParseActor(string value)
    {
        if (!AuthorityActorId.TryParse(value, out var actor, out _))
        {
            throw new ArgumentException("The Human Input response actor identity is invalid.", nameof(value));
        }

        return actor!;
    }
}
