using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequestStoreTestData;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

public sealed class HumanInputRequestStoreInvariantTests
{
    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede)]
    public async Task Concurrent_same_head_transitions_have_one_winner_and_one_durable_restart_safe_conflict(
        HumanInputRequestLifecycleOperationKind kind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(created)).Status);
        var (first, second) = CompetingMutations(kind, created.RequestToAppend!, created.PrimaryHeadToWrite!);

        var outcomes = await Task.WhenAll(Store(paths, trust).CommitAsync(first), Store(paths, trust).CommitAsync(second));

        Assert.Single(outcomes, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.Committed);
        Assert.Single(outcomes, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.StoreConflict);
        var firstWon = outcomes[0].Status == HumanInputRequestLifecycleStoreCommitStatus.Committed;
        var winner = firstWon ? first : second;
        var loser = firstWon ? second : first;
        var winnerRead = await Store(paths, trust).ReadAsync(created.Operation.TargetRequestId);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, winnerRead.Status);
        Assert.Equal(winner.PrimaryHeadToWrite, winnerRead.PrimarySnapshot!.Head);
        var receipt = OptimisticConflictReceipt(loser, winnerRead.PrimarySnapshot.Head, winnerRead.StoreGeneration);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(receipt)).Status);
        var restarted = Store(paths, trust);
        var final = await restarted.ReadAsync(created.Operation.TargetRequestId);
        var replayed = await restarted.CommitAsync(receipt);

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, final.Status);
        Assert.Equal(3, final.StoreGeneration);
        Assert.Equal(winner.PrimaryHeadToWrite, final.PrimarySnapshot!.Head);
        Assert.Equal(3, final.PrimarySnapshot.Operations.Count);
        Assert.Equal(receipt.Operation, final.PrimarySnapshot.Operations[^1]);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(winner.PrimaryHeadToWrite, replayed.PrimarySnapshot!.Head);
        if (winner.RequestToAppend is { } winnerRequest)
        {
            var winnerLifecycle = await restarted.ReadAsync(winnerRequest.RequestId);
            Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, winnerLifecycle.Status);
            Assert.Contains(winnerLifecycle.PrimarySnapshot!.RequestVersions, request => request.RequestHash == winnerRequest.RequestHash);
        }
        if (kind == HumanInputRequestLifecycleOperationKind.Supersede)
        {
            Assert.Equal(
                HumanInputRequestLifecycleStoreReadStatus.NotFound,
                (await restarted.ReadAsync(loser.Operation.RelatedRequestId!)).Status);
        }
    }

    [Fact]
    public async Task Noncommitted_candidate_reference_reserves_version_identity_against_hash_substitution_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var attempted = TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            created.RequestToAppend!,
            created.PrimaryHeadToWrite!,
            2,
            "amend-after-reserved-reference",
            HashC);
        var reservedCandidate = attempted.RequestToAppend!;
        var receiptEvidence = attempted.Operation with
        {
            OperationId = "reserve-amend-candidate",
            RequestHash = HashB,
            Outcome = HumanInputRequestLifecycleOperationOutcome.Conflict,
            FailureCode = HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict,
            ResultHead = attempted.Operation.PreviousHead
        };
        var receipt = new HumanInputRequestLifecycleStoreMutation(1, receiptEvidence, null, null, null);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(receipt)).Status);
        var substitutedCandidate = Rehash(reservedCandidate with { Prompt = "Different private amended prompt." });
        Assert.NotEqual(reservedCandidate.RequestHash, substitutedCandidate.RequestHash);
        var substitutedHead = attempted.PrimaryHeadToWrite! with { CurrentRequest = Reference(substitutedCandidate) };
        var substitution = attempted with
        {
            Operation = attempted.Operation with
            {
                CandidateRequest = Reference(substitutedCandidate),
                ResultHead = substitutedHead
            },
            RequestToAppend = substitutedCandidate,
            PrimaryHeadToWrite = substitutedHead
        };

        var rejected = await store.CommitAsync(substitution);
        var restarted = Store(paths, trust);
        var recovered = await restarted.ReadAsync(created.Operation.TargetRequestId);
        var rejectedAfterRestart = await restarted.CommitAsync(substitution);
        var replayedReceipt = await restarted.CommitAsync(receipt);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, rejected.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, rejectedAfterRestart.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(created.PrimaryHeadToWrite, recovered.PrimarySnapshot!.Head);
        Assert.Single(recovered.PrimarySnapshot.RequestVersions);
        Assert.Equal(2, recovered.PrimarySnapshot.Operations.Count);
        Assert.Equal(receipt.Operation, recovered.PrimarySnapshot.Operations[^1]);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayedReceipt.Status);
    }

    [Theory]
    [InlineData("generation-above-schema-bound")]
    [InlineData("distinct-operation-count-exceeds-generation")]
    public async Task Authenticated_impossible_global_generation_is_quarantined_after_restart(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        if (corruption == "distinct-operation-count-exceeds-generation")
        {
            Assert.Equal(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                (await store.CommitAsync(TransitionMutation(
                    HumanInputRequestLifecycleOperationKind.Remind,
                    created.RequestToAppend!,
                    created.PrimaryHeadToWrite!,
                    1,
                    "remind-for-generation-corruption",
                    HashB))).Status);
        }
        var pinned = await RewriteAuthenticatedAsync(paths, root => root["generation"] = corruption switch
        {
            "generation-above-schema-bound" => HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore + 1L,
            "distinct-operation-count-exceeds-generation" => 1L,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        });

        var read = await Store(paths, pinned).ReadAsync(created.Operation.TargetRequestId);

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    [Theory]
    [InlineData(false, "Unknown")]
    [InlineData(true, "Pending")]
    public async Task Authenticated_case_changed_expected_lifecycle_status_is_quarantined(
        bool appendTransition,
        string corruptedToken)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        if (appendTransition)
        {
            Assert.Equal(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                (await store.CommitAsync(TransitionMutation(
                    HumanInputRequestLifecycleOperationKind.Remind,
                    created.RequestToAppend!,
                    created.PrimaryHeadToWrite!,
                    1,
                    "remind-before-status-corruption",
                    HashB))).Status);
        }
        var operationIndex = appendTransition ? 1 : 0;
        var pinned = await RewriteAuthenticatedAsync(
            paths,
            root => root["operations"]!.AsArray()[operationIndex]!.AsObject()["expectedLifecycleStatus"] = corruptedToken);

        var read = await Store(paths, pinned).ReadAsync(created.Operation.TargetRequestId);

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    private static (HumanInputRequestLifecycleStoreMutation First, HumanInputRequestLifecycleStoreMutation Second) CompetingMutations(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest previousRequest,
        HumanInputRequestLifecycleHead previousHead)
    {
        if (kind == HumanInputRequestLifecycleOperationKind.Supersede)
        {
            var firstSupersede = SupersedeMutation(previousRequest, previousHead, 1, "supersede-race-one", HashB);
            var secondSupersede = SupersedeMutation(previousRequest, previousHead, 1, "supersede-race-two", HashC);
            var secondCandidate = Rehash(secondSupersede.RequestToAppend! with
            {
                RequestId = "request-three",
                RequestVersionId = "version-three",
                Prompt = "Private second replacement prompt."
            });
            return (firstSupersede, ReplaceSupersedeCandidate(secondSupersede, secondCandidate));
        }

        var first = TransitionMutation(kind, previousRequest, previousHead, 1, "transition-race-one", HashB);
        var second = TransitionMutation(kind, previousRequest, previousHead, 1, "transition-race-two", HashC);
        if (kind == HumanInputRequestLifecycleOperationKind.Amend)
        {
            var candidate = Rehash(second.RequestToAppend! with
            {
                RequestVersionId = "version-amended-two",
                Prompt = "Private second amended prompt."
            });
            second = ReplaceSingleRequestCandidate(second, candidate);
        }
        else if (kind == HumanInputRequestLifecycleOperationKind.Reroute)
        {
            var candidate = Rehash(second.RequestToAppend! with
            {
                RequestVersionId = "version-rerouted-two",
                EligibleRespondents = [new HumanInputEligibleRespondent("user-three", "route-three")]
            });
            second = ReplaceSingleRequestCandidate(second, candidate);
        }

        return (first, second);
    }

    private static HumanInputRequestLifecycleStoreMutation ReplaceSingleRequestCandidate(
        HumanInputRequestLifecycleStoreMutation mutation,
        HumanInputRequest candidate)
    {
        var head = mutation.PrimaryHeadToWrite! with { CurrentRequest = Reference(candidate) };
        return mutation with
        {
            Operation = mutation.Operation with { CandidateRequest = Reference(candidate), ResultHead = head },
            RequestToAppend = candidate,
            PrimaryHeadToWrite = head
        };
    }

    private static HumanInputRequestLifecycleStoreMutation ReplaceSupersedeCandidate(
        HumanInputRequestLifecycleStoreMutation mutation,
        HumanInputRequest candidate)
    {
        var primary = mutation.PrimaryHeadToWrite! with { SupersededByRequestId = candidate.RequestId };
        var secondary = mutation.SecondaryHeadToWrite! with
        {
            RequestId = candidate.RequestId,
            CurrentRequest = Reference(candidate)
        };
        return mutation with
        {
            Operation = mutation.Operation with
            {
                ResultHead = primary,
                RelatedRequestId = candidate.RequestId,
                RelatedResultHead = secondary,
                CandidateRequest = Reference(candidate)
            },
            RequestToAppend = candidate,
            PrimaryHeadToWrite = primary,
            SecondaryHeadToWrite = secondary
        };
    }

    private static HumanInputRequestLifecycleStoreMutation OptimisticConflictReceipt(
        HumanInputRequestLifecycleStoreMutation loser,
        HumanInputRequestLifecycleHead currentHead,
        long generation)
    {
        var evidence = loser.Operation with
        {
            Outcome = HumanInputRequestLifecycleOperationOutcome.Conflict,
            FailureCode = HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict,
            PreviousHead = currentHead,
            ResultHead = currentHead,
            RelatedPreviousHead = null,
            RelatedResultHead = null,
            RecordedAtUtc = currentHead.UpdatedAtUtc.AddTicks(1)
        };
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, null, null, null);
    }

    private static HumanInputRequestStore Store(WorkspacePaths paths, ICapabilityCatalogTrustProvider trust)
        => new(paths, trust);

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "human-input", "requests", "lifecycle.json");

    private static async Task<ICapabilityCatalogTrustProvider> RewriteAuthenticatedAsync(
        WorkspacePaths paths,
        Action<JsonObject> mutate)
    {
        var path = PrimaryPath(paths);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        mutate(root);
        root["contentDigest"] = string.Empty;
        root["authenticationTag"] = string.Empty;
        var canonical = root.ToJsonString();
        var contentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(canonical)).Value;
        const string AuthenticationTag = "pinned-human-input-invariant-document";
        root["contentDigest"] = contentDigest;
        root["authenticationTag"] = AuthenticationTag;
        await File.WriteAllTextAsync(path, root.ToJsonString() + Environment.NewLine);
        return new HumanInputPinnedTrustProvider(
            root["workspaceIdentity"]!.GetValue<string>(),
            root["generation"]!.GetValue<long>(),
            contentDigest,
            AuthenticationTag);
    }
}
