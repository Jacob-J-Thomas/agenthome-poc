using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Tests.Loops.Execution.Authority;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityEvidenceStoreTests
{
    [Fact]
    public async Task Append_restart_and_exact_replay_preserve_one_authenticated_immutable_decision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var decision = Decision("effect-operation-one");
        var firstTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);

        var appended = await Store(paths, firstTrust).AppendAsync(decision);
        var replayed = await Store(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)).AppendAsync(decision);
        var primary = PrimaryPath(paths);
        var content = await File.ReadAllTextAsync(primary);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, appended.Status);
        Assert.Equal(decision.ContentHash, appended.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
        Assert.Contains("\"effectOperationId\": \"effect-operation-one\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);
        Assert.False(content.StartsWith('\ufeff'));
        Assert.True(File.Exists(ProofPath(paths)));
    }

    [Fact]
    public async Task Same_effect_identity_with_any_different_coordinates_conflicts_without_replacement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var original = Decision("effect-operation-one");
        var changed = Rehash(original with
        {
            RunId = "run-two",
            CorrelationId = "provider-request-two"
        });
        var store = Store(paths, trust);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(original)).Status);
        var conflict = await store.AppendAsync(changed);
        var replay = await store.AppendAsync(original);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict, conflict.Status);
        Assert.Equal(original.ContentHash, conflict.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replay.Status);
        Assert.Equal(original.ContentHash, replay.ContentHash);
        Assert.DoesNotContain("run-two", await File.ReadAllTextAsync(PrimaryPath(paths)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_exact_writers_append_once_and_replay_once()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var decision = Decision("effect-operation-one");

        var results = await Task.WhenAll(Store(paths, trust).AppendAsync(decision), Store(paths, trust).AppendAsync(decision));

        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended);
        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent);
        Assert.All(results, result => Assert.Equal(decision.ContentHash, result.ContentHash));
    }

    [Fact]
    public async Task Distinct_effects_append_without_rewriting_prior_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var first = Decision("effect-operation-one");
        var second = Decision("effect-operation-two");
        var store = Store(paths, trust);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(first)).Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(second)).Status);
        var firstReplay = await Store(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath))
            .AppendAsync(first);
        var content = await File.ReadAllTextAsync(PrimaryPath(paths));

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, firstReplay.Status);
        Assert.Equal(first.ContentHash, firstReplay.ContentHash);
        Assert.Contains("effect-operation-one", content, StringComparison.Ordinal);
        Assert.Contains("effect-operation-two", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_decision_and_exhausted_quotas_fail_before_new_evidence()
    {
        using var invalidWorkspace = new TestWorkspace();
        var invalidPaths = new WorkspacePaths(invalidWorkspace.RootPath);
        var invalid = Decision("effect-operation-one") with { ContentHash = new string('f', 64) };
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(invalidPaths, new TestCapabilityLifecycleTrustProvider()).AppendAsync(invalid)).Status);
        Assert.False(File.Exists(PrimaryPath(invalidPaths)));

        using var quotaWorkspace = new TestWorkspace();
        var quotaPaths = new WorkspacePaths(quotaWorkspace.RootPath);
        var quotaStore = Store(
            quotaPaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxDecisions = 1 });
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            (await quotaStore.AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await quotaStore.AppendAsync(Decision("effect-operation-two"))).Status);
        Assert.DoesNotContain("effect-operation-two", await File.ReadAllTextAsync(PrimaryPath(quotaPaths)), StringComparison.Ordinal);

        using var byteWorkspace = new TestWorkspace();
        var bytePaths = new WorkspacePaths(byteWorkspace.RootPath);
        var byteStore = Store(
            bytePaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxArtifactUtf8Bytes = 1 });
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await byteStore.AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.False(File.Exists(PrimaryPath(bytePaths)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("enum-case")]
    [InlineData("authentication")]
    [InlineData("schema")]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    public async Task Corrupt_or_noncanonical_authenticated_ledger_is_quarantined_as_ambiguous(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var original = Decision("effect-operation-one");
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await Store(paths, trust).AppendAsync(original)).Status);
        var primary = PrimaryPath(paths);
        var text = await File.ReadAllTextAsync(primary);
        switch (corruption)
        {
            case "unknown":
                await File.WriteAllTextAsync(primary, text.Replace("\"workspaceIdentity\":", "\"unknown\": true,\n  \"workspaceIdentity\":", StringComparison.Ordinal));
                break;
            case "duplicate":
                await File.WriteAllTextAsync(primary, text.Replace("\"generation\": 1", "\"generation\": 1,\n  \"generation\": 1", StringComparison.Ordinal));
                break;
            case "enum-case":
                await File.WriteAllTextAsync(primary, text.Replace("\"provider-transport\"", "\"ProviderTransport\"", StringComparison.Ordinal));
                break;
            case "authentication":
                var tagIndex = text.IndexOf("\"authenticationTag\": \"test:", StringComparison.Ordinal);
                Assert.True(tagIndex >= 0);
                var tagCharacter = tagIndex + "\"authenticationTag\": \"test:".Length;
                var replacement = text[tagCharacter] == 'a' ? 'b' : 'a';
                await File.WriteAllTextAsync(primary, text[..tagCharacter] + replacement + text[(tagCharacter + 1)..]);
                break;
            case "schema":
                await File.WriteAllTextAsync(primary, text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
                break;
            case "bom":
                await File.WriteAllBytesAsync(primary, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)]);
                break;
            case "invalid-utf8":
                await File.WriteAllBytesAsync(primary, [0xff, 0xfe, 0xfd]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var exact = await Store(paths, trust).AppendAsync(original);
        var unrelated = await Store(paths, trust).AppendAsync(Decision("effect-operation-two"));

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, exact.Status);
        Assert.Null(exact.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, unrelated.Status);
        Assert.Null(unrelated.ContentHash);
        Assert.DoesNotContain("effect-operation-two", await File.ReadAllTextAsync(primary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_pending_successor_is_finalized_before_an_unrelated_effect_appends()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Decision("effect-operation-one");
        var second = Decision("effect-operation-two");
        var interrupted = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished
                ? ValueTask.FromException(new IOException("Injected process loss after primary publication."))
                : ValueTask.CompletedTask
        };

        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous,
            (await Store(paths, trust, interrupted).AppendAsync(first)).Status);
        var unrelated = await Store(paths, trust).AppendAsync(second);
        var firstReplay = await Store(paths, trust).AppendAsync(first);
        var secondReplay = await Store(paths, trust).AppendAsync(second);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, unrelated.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, firstReplay.Status);
        Assert.Equal(first.ContentHash, firstReplay.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, secondReplay.Status);
        Assert.Equal(second.ContentHash, secondReplay.ContentHash);
    }

    [Fact]
    public async Task Target_budget_is_shared_across_retries_nodes_generations_boundaries_and_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Usage("target-one", "target-operation-one");
        var sameTargetLater = Usage(
            "target-one",
            "target-operation-two",
            executionGeneration: 2,
            nodeId: "inference-2",
            nodeAttempt: 2,
            boundaryKind: GovernedLoopEffectBoundaryKind.WorkspaceActuation);
        var differentTarget = Usage("target-two", "target-operation-three", executionGeneration: 3, nodeId: "inference-3");

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await Store(paths, trust).ReserveAsync(first)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved,
            (await Store(paths, trust).ReserveAsync(sameTargetLater)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded,
            (await Store(paths, trust).ReserveAsync(differentTarget)).Status);

        var content = await File.ReadAllTextAsync(PrimaryPath(paths));
        Assert.Contains("target-operation-one", content, StringComparison.Ordinal);
        Assert.DoesNotContain("target-operation-two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("target-operation-three", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_distinct_targets_with_a_one_target_ceiling_reserve_exactly_one()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();

        var results = await Task.WhenAll(
            Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one")),
            Store(paths, trust).ReserveAsync(Usage("target-two", "target-operation-two")));

        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved);
        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded);
    }

    [Fact]
    public async Task Concurrent_intake_and_actuation_of_the_same_server_target_share_one_reservation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var intake = Usage("target-one", "target-operation-one", boundaryKind: GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);
        var actuation = Usage("target-one", "target-operation-two", boundaryKind: GovernedLoopEffectBoundaryKind.WorkspaceActuation);

        var results = await Task.WhenAll(
            Store(paths, trust).ReserveAsync(intake),
            Store(paths, trust).ReserveAsync(actuation));

        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved);
        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved);
    }

    [Fact]
    public async Task Conversation_publication_and_workspace_targets_share_the_exact_grant_run_budget()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var workspaceTarget = Usage("target-one", "target-operation-one");
        var conversationTarget = Usage(
            "conversation-target",
            "conversation-operation-one",
            boundaryKind: GovernedLoopEffectBoundaryKind.ConversationPublication);

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await Store(paths, trust).ReserveAsync(workspaceTarget)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded,
            (await Store(paths, trust).ReserveAsync(conversationTarget)).Status);
    }

    [Fact]
    public async Task Pending_and_completed_first_run_claims_fail_closed_across_restart_and_bound_runs()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var completion = Completion();

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await Store(paths, trust).BeginCompletionAsync(completion)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending,
            (await Store(paths, trust).BeginCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(1) })).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
            (await Store(paths, trust).ReserveAsync(Usage("target-one", "other-run-effect", runId: "run-2"))).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted,
            (await Store(paths, trust).CompleteCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(2) })).Status);

        var restarted = Store(paths, trust);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted,
            (await restarted.BeginCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(3) })).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted,
            (await restarted.ReserveAsync(Usage("target-one", "later-run-effect", runId: "run-3"))).Status);
    }

    [Fact]
    public async Task Completion_and_effect_dispatch_race_is_serialized_without_crossing_a_pending_claim()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var completion = Completion();
        var effect = Usage("target-one", "target-operation-one");

        var results = await Task.WhenAll(
            Store(paths, trust).BeginCompletionAsync(completion),
            Store(paths, trust).ReserveAsync(effect));

        Assert.Contains(results, result => result.Status == GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending);
        var effectResult = results.Single(result => result.Status != GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending);
        Assert.Contains(
            effectResult.Status,
            new[]
            {
                GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
                GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
            });
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
            (await Store(paths, trust).ReserveAsync(effect with { EffectOperationId = "later-effect" })).Status);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved)]
    public async Task Target_reservation_recovers_conservatively_after_each_durable_boundary(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        GovernedLoopEffectAuthorityUsageStoreStatus interruptedStatus,
        GovernedLoopEffectAuthorityUsageStoreStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var request = Usage("target-one", "target-operation-one");
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

        var interrupted = await Store(paths, trust, options).ReserveAsync(request);
        var retried = await Store(paths, trust).ReserveAsync(request with { EvaluatedAtUtc = request.EvaluatedAtUtc.AddSeconds(1) });

        Assert.Equal(interruptedStatus, interrupted.Status);
        Assert.Equal(retryStatus, retried.Status);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending)]
    public async Task Pending_completion_claim_recovers_conservatively_after_each_durable_boundary(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        GovernedLoopEffectAuthorityUsageStoreStatus interruptedStatus,
        GovernedLoopEffectAuthorityUsageStoreStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var request = Completion();
        var options = FailingAt(boundary);

        var interrupted = await Store(paths, trust, options).BeginCompletionAsync(request);
        var retried = await Store(paths, trust).BeginCompletionAsync(request with { EvaluatedAtUtc = request.EvaluatedAtUtc.AddSeconds(1) });

        Assert.Equal(interruptedStatus, interrupted.Status);
        Assert.Equal(retryStatus, retried.Status);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted)]
    public async Task Completed_claim_recovers_conservatively_after_terminal_callback_crash_boundaries(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        GovernedLoopEffectAuthorityUsageStoreStatus interruptedStatus,
        GovernedLoopEffectAuthorityUsageStoreStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var request = Completion();
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await Store(paths, trust).BeginCompletionAsync(request)).Status);

        var complete = request with { EvaluatedAtUtc = request.EvaluatedAtUtc.AddSeconds(1) };
        var interrupted = await Store(paths, trust, FailingAt(boundary)).CompleteCompletionAsync(complete);
        var retried = await Store(paths, trust).CompleteCompletionAsync(complete with { EvaluatedAtUtc = complete.EvaluatedAtUtc.AddSeconds(1) });

        Assert.Equal(interruptedStatus, interrupted.Status);
        Assert.Equal(retryStatus, retried.Status);
    }

    [Fact]
    public async Task An_unclaimed_failed_run_does_not_consume_a_later_bound_run()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();

        var laterRun = await Store(paths, trust).ReserveAsync(Usage("target-one", "later-run-effect", runId: "run-2"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved, laterRun.Status);
    }

    [Fact]
    public async Task Invalid_and_non_target_usage_requests_fail_closed_without_consuming_usage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var invalidTarget = Usage("target-one", "invalid-target-operation") with { SchemaVersion = 2 };
        var invalidCompletion = Completion() with { SchemaVersion = 2 };
        var provider = Usage("unused", "provider-operation") with
        {
            BoundaryKind = GovernedLoopEffectBoundaryKind.ProviderTransport,
            MaxTargetCount = 0,
            TargetFingerprint = null,
        };

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
            (await store.ReserveAsync(invalidTarget)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
            (await store.BeginCompletionAsync(invalidCompletion)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Conflict,
            (await store.CompleteCompletionAsync(Completion())).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Allowed,
            (await store.ReserveAsync(provider)).Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Target_identity_conflicts_and_global_usage_quotas_never_replace_evidence()
    {
        using var identityWorkspace = new TestWorkspace();
        var identityPaths = new WorkspacePaths(identityWorkspace.RootPath);
        var identityStore = Store(identityPaths, new TestCapabilityLifecycleTrustProvider());
        var original = Usage("target-one", "target-operation-one");
        var operationCollision = Usage("target-two", original.EffectOperationId);
        var scopeCollision = Usage("target-two", "target-operation-two") with
        {
            AdmissionReceiptHash = Hash("other-admission-receipt"),
        };

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await identityStore.ReserveAsync(original)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Conflict,
            (await identityStore.ReserveAsync(operationCollision)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Conflict,
            (await identityStore.ReserveAsync(scopeCollision)).Status);
        var identityEvidence = await File.ReadAllTextAsync(PrimaryPath(identityPaths));
        Assert.Contains(original.TargetFingerprint!, identityEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(operationCollision.TargetFingerprint!, identityEvidence, StringComparison.Ordinal);

        using var targetQuotaWorkspace = new TestWorkspace();
        var targetQuotaPaths = new WorkspacePaths(targetQuotaWorkspace.RootPath);
        var targetQuotaStore = Store(
            targetQuotaPaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxTargetReservations = 1 });
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await targetQuotaStore.ReserveAsync(original)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
            (await targetQuotaStore.ReserveAsync(Usage("target-two", "other-run-operation", runId: "run-2"))).Status);

        using var completionQuotaWorkspace = new TestWorkspace();
        var completionQuotaPaths = new WorkspacePaths(completionQuotaWorkspace.RootPath);
        var completionQuotaStore = Store(
            completionQuotaPaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxCompletionClaims = 1 });
        var completion = Completion();
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await completionQuotaStore.BeginCompletionAsync(completion)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
            (await completionQuotaStore.CompleteCompletionAsync(completion)).Status);

        using var byteQuotaWorkspace = new TestWorkspace();
        var byteQuotaPaths = new WorkspacePaths(byteQuotaWorkspace.RootPath);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
            (await Store(
                byteQuotaPaths,
                new TestCapabilityLifecycleTrustProvider(),
                new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxArtifactUtf8Bytes = 1 })
                .ReserveAsync(original)).Status);
        Assert.False(File.Exists(PrimaryPath(byteQuotaPaths)));
    }

    [Fact]
    public async Task Completion_identity_conflicts_preserve_the_first_bound_run_claim()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var first = Completion();
        var other = first with
        {
            RunId = "run-2",
            CompletionOperationId = "run-completion-two",
        };

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await store.BeginCompletionAsync(first)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
            (await store.BeginCompletionAsync(other)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.Conflict,
            (await store.CompleteCompletionAsync(other)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted,
            (await store.CompleteCompletionAsync(first)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted,
            (await store.CompleteCompletionAsync(other)).Status);
    }

    [Fact]
    public async Task Cross_generation_completion_conflicts_without_mutating_the_pending_claim()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var firstGeneration = Completion(executionGeneration: 1);

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await store.BeginCompletionAsync(firstGeneration)).Status);
        var pendingEvidence = await File.ReadAllTextAsync(PrimaryPath(paths));
        var laterGeneration = firstGeneration with
        {
            ExecutionGeneration = 2,
            EvaluatedAtUtc = firstGeneration.EvaluatedAtUtc.AddSeconds(1),
        };

        var conflict = await store.CompleteCompletionAsync(laterGeneration);

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Conflict, conflict.Status);
        Assert.Equal(pendingEvidence, await File.ReadAllTextAsync(PrimaryPath(paths)));
        Assert.DoesNotContain("\"executionGeneration\": 2", pendingEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_same_generation_completion_replays_are_idempotent_without_evidence_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var completion = Completion(executionGeneration: 7);

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await store.BeginCompletionAsync(completion)).Status);
        var pendingEvidence = await File.ReadAllTextAsync(PrimaryPath(paths));
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending,
            (await store.BeginCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(1) })).Status);
        Assert.Equal(pendingEvidence, await File.ReadAllTextAsync(PrimaryPath(paths)));

        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted,
            (await store.CompleteCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(2) })).Status);
        var completedEvidence = await File.ReadAllTextAsync(PrimaryPath(paths));
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted,
            (await store.CompleteCompletionAsync(completion with { EvaluatedAtUtc = completion.EvaluatedAtUtc.AddSeconds(3) })).Status);
        Assert.Equal(completedEvidence, await File.ReadAllTextAsync(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Authenticated_completed_claim_with_a_substituted_execution_generation_is_quarantined()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var completion = Completion(executionGeneration: 1);
        var originalTrust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, originalTrust);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
            (await store.BeginCompletionAsync(completion)).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted,
            (await store.CompleteCompletionAsync(completion)).Status);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(PrimaryPath(paths)))!.AsObject();
        var claims = document["completionClaims"]!.AsArray();
        Assert.Equal(2, claims.Count);
        claims[1]!["executionGeneration"] = completion.ExecutionGeneration + 1;
        document["contentDigest"] = string.Empty;
        document["authenticationTag"] = string.Empty;
        var digest = Hash(document.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var workspaceIdentity = document["workspaceIdentity"]!.GetValue<string>();
        var generation = document["generation"]!.GetValue<long>();
        var substitutedTrust = new TestCapabilityLifecycleTrustProvider();
        document["contentDigest"] = digest;
        document["authenticationTag"] = await substitutedTrust.AuthenticateArtifactAsync(workspaceIdentity, generation, digest);
        var substituted = document.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n";
        await File.WriteAllTextAsync(PrimaryPath(paths), substituted);
        _ = await substitutedTrust.InitializeAsync(workspaceIdentity, generation, digest);

        var result = await Store(paths, substitutedTrust).CompleteCompletionAsync(completion);

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, result.Status);
        Assert.Equal(substituted, await File.ReadAllTextAsync(PrimaryPath(paths)));
    }

    [Theory]
    [InlineData(CapabilityAuthorityTransactionFault.CancelBeforeCallback, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable)]
    [InlineData(CapabilityAuthorityTransactionFault.CancelAfterCallback, GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved)]
    [InlineData(CapabilityAuthorityTransactionFault.IoAfterCallback, GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved)]
    public async Task Usage_transaction_failures_report_only_proven_callback_results(
        CapabilityAuthorityTransactionFault fault,
        GovernedLoopEffectAuthorityUsageStoreStatus expected)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var transaction = new FaultingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths), fault);

        var result = await Store(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            authorityTransaction: transaction)
            .ReserveAsync(Usage("target-one", "target-operation-one"));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Usage_cancellation_propagates_before_the_authority_callback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(
                paths,
                new TestCapabilityLifecycleTrustProvider())
            .ReserveAsync(Usage("target-one", "target-operation-one"), new CancellationToken(canceled: true)));
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Caller_cancellation_during_trust_read_propagates_before_durable_intent()
    {
        using var appendWorkspace = new TestWorkspace();
        var appendCancellation = new CancellationTokenSource();
        var appendTrust = new TestCapabilityLifecycleTrustProvider
        {
            BeforeRead = _ => appendCancellation.Cancel(),
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(
                new WorkspacePaths(appendWorkspace.RootPath),
                appendTrust)
            .AppendAsync(Decision("effect-operation-one"), appendCancellation.Token));

        using var usageWorkspace = new TestWorkspace();
        var usageCancellation = new CancellationTokenSource();
        var usageTrust = new TestCapabilityLifecycleTrustProvider
        {
            BeforeRead = _ => usageCancellation.Cancel(),
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(
                new WorkspacePaths(usageWorkspace.RootPath),
                usageTrust)
            .ReserveAsync(Usage("target-one", "target-operation-one"), usageCancellation.Token));
    }

    [Theory]
    [InlineData(AdversarialTrustBehavior.MismatchedInitialization, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable)]
    [InlineData(AdversarialTrustBehavior.EmptyAuthenticationTag, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable)]
    [InlineData(AdversarialTrustBehavior.MismatchedSuccessor, GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous)]
    [InlineData(AdversarialTrustBehavior.WrongWorkspaceRead, GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable)]
    public async Task Adversarial_trust_responses_never_authorize_usage(
        AdversarialTrustBehavior behavior,
        GovernedLoopEffectAuthorityUsageStoreStatus expected)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new AdversarialEffectAuthorityTrustProvider(new TestCapabilityLifecycleTrustProvider(), behavior);

        var result = await Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one"));

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(AdversarialTrustBehavior.MismatchedInitialization, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable)]
    [InlineData(AdversarialTrustBehavior.EmptyAuthenticationTag, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable)]
    [InlineData(AdversarialTrustBehavior.MismatchedSuccessor, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous)]
    public async Task Adversarial_trust_responses_never_publish_effect_decisions(
        AdversarialTrustBehavior behavior,
        GovernedLoopEffectAuthorityEvidenceStoreStatus expected)
    {
        using var workspace = new TestWorkspace();
        var trust = new AdversarialEffectAuthorityTrustProvider(new TestCapabilityLifecycleTrustProvider(), behavior);

        var result = await Store(new WorkspacePaths(workspace.RootPath), trust)
            .AppendAsync(Decision("effect-operation-one"));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Corrupt_pending_usage_successor_is_quarantined_without_recovery_as_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Usage("target-one", "target-operation-one", runId: "run-1");
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await Store(paths, trust).ReserveAsync(first)).Status);
        var interrupted = await Store(paths, trust, FailingAt(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished))
            .ReserveAsync(Usage("target-two", "target-operation-two", runId: "run-2"));
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, interrupted.Status);

        var primary = PrimaryPath(paths);
        var text = await File.ReadAllTextAsync(primary);
        await File.WriteAllTextAsync(primary, text.Replace("\"targetReservations\":", "\"targetReservationsCorrupt\":", StringComparison.Ordinal));

        var result = await Store(paths, trust).ReserveAsync(Usage("target-three", "target-operation-three", runId: "run-3"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, result.Status);
        Assert.DoesNotContain("target-operation-three", await File.ReadAllTextAsync(primary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previous_generation_primary_is_recovery_evidence_not_current_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await store.ReserveAsync(Usage("target-one", "target-operation-one", runId: "run-1"))).Status);
        var previous = await File.ReadAllTextAsync(PrimaryPath(paths));
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await store.ReserveAsync(Usage("target-two", "target-operation-two", runId: "run-2"))).Status);
        await File.WriteAllTextAsync(PrimaryPath(paths), previous);
        File.Delete(ProofPath(paths));

        var result = await Store(paths, trust)
            .ReserveAsync(Usage("target-three", "target-operation-three", runId: "run-3"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, result.Status);
    }

    [Theory]
    [InlineData("document-schema")]
    [InlineData("decision-contract")]
    [InlineData("nested-duplicate")]
    [InlineData("reservation-contract")]
    [InlineData("completion-contract")]
    [InlineData("completion-sequence")]
    public async Task Malformed_usage_ledger_records_are_never_recovered_as_authority(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        string text;
        switch (corruption)
        {
            case "document-schema":
                Assert.Equal(
                    GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
                    (await Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one"))).Status);
                text = ReplaceFirst(
                    await File.ReadAllTextAsync(PrimaryPath(paths)),
                    "\"schemaVersion\": 1",
                    "\"schemaVersion\": 2");
                break;
            case "decision-contract":
                Assert.Equal(
                    GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                    (await Store(paths, trust).AppendAsync(Decision("effect-operation-one"))).Status);
                text = (await File.ReadAllTextAsync(PrimaryPath(paths))).Replace(
                    "\"effectOperationId\": \"effect-operation-one\"",
                    "\"effectOperationId\": \"INVALID OPERATION\"",
                    StringComparison.Ordinal);
                break;
            case "nested-duplicate":
                Assert.Equal(
                    GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
                    (await Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one"))).Status);
                text = (await File.ReadAllTextAsync(PrimaryPath(paths))).Replace(
                    "\"runId\": \"run-1\"",
                    "\"runId\": \"run-1\",\n      \"runId\": \"run-1\"",
                    StringComparison.Ordinal);
                break;
            case "reservation-contract":
                var reservation = Usage("target-one", "target-operation-one");
                Assert.Equal(
                    GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
                    (await Store(paths, trust).ReserveAsync(reservation)).Status);
                text = (await File.ReadAllTextAsync(PrimaryPath(paths))).Replace(
                    reservation.TargetFingerprint!,
                    reservation.TargetFingerprint!.ToUpperInvariant(),
                    StringComparison.Ordinal);
                break;
            case "completion-contract":
                var invalidCompletion = Completion();
                Assert.Equal(
                    GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
                    (await Store(paths, trust).BeginCompletionAsync(invalidCompletion)).Status);
                text = (await File.ReadAllTextAsync(PrimaryPath(paths))).Replace(
                    invalidCompletion.AdmissionReceiptHash,
                    invalidCompletion.AdmissionReceiptHash.ToUpperInvariant(),
                    StringComparison.Ordinal);
                break;
            case "completion-sequence":
                Assert.Equal(
                    GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending,
                    (await Store(paths, trust).BeginCompletionAsync(Completion())).Status);
                text = (await File.ReadAllTextAsync(PrimaryPath(paths))).Replace(
                    "\"status\": \"pending\"",
                    "\"status\": \"completed\"",
                    StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        await File.WriteAllTextAsync(PrimaryPath(paths), text);
        var result = await Store(paths, trust)
            .ReserveAsync(Usage("target-two", "next-operation", runId: "run-2"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, result.Status);
        Assert.DoesNotContain("next-operation", await File.ReadAllTextAsync(PrimaryPath(paths)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_primary_and_proof_fail_closed_as_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one"))).Status);
        await File.WriteAllBytesAsync(PrimaryPath(paths), [0xff]);
        await File.WriteAllBytesAsync(ProofPath(paths), [0xff]);

        var result = await Store(paths, trust)
            .ReserveAsync(Usage("target-two", "target-operation-two", runId: "run-2"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Path_observer_failure_during_bound_read_fails_before_usage_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            PathObserver = new ThrowAfterEffectAuthorityFileOpenObserver(),
        };

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider(), options)
            .ReserveAsync(Usage("target-one", "target-operation-one"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, result.Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Theory]
    [InlineData("primary-directory")]
    [InlineData("proof-directory")]
    [InlineData("lock-directory")]
    [InlineData("primary-symlink")]
    [InlineData("primary-fifo")]
    public async Task Unsafe_native_usage_artifact_shapes_fail_closed_without_following_them(string shape)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(EffectAuthorityRoot(paths));
        switch (shape)
        {
            case "primary-directory":
                Directory.CreateDirectory(PrimaryPath(paths));
                break;
            case "proof-directory":
                Directory.CreateDirectory(ProofPath(paths));
                break;
            case "lock-directory":
                Directory.CreateDirectory(LockPath(paths));
                break;
            case "primary-symlink":
                var outside = workspace.File("outside-evidence.json");
                await File.WriteAllTextAsync(outside, "outside-canary");
                try
                {
                    File.CreateSymbolicLink(PrimaryPath(paths), outside);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    return;
                }

                break;
            case "primary-fifo":
                if (!CapabilityCatalogUnixFifo.TryCreate(PrimaryPath(paths)))
                {
                    return;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider())
            .ReserveAsync(Usage("target-one", "target-operation-one"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, result.Status);
        if (shape == "primary-symlink")
        {
            Assert.Equal("outside-canary", await File.ReadAllTextAsync(workspace.File("outside-evidence.json")));
        }
    }

    [Fact]
    public async Task Hard_linked_usage_evidence_is_rejected_before_a_later_reservation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        Assert.Equal(
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved,
            (await Store(paths, trust).ReserveAsync(Usage("target-one", "target-operation-one"))).Status);
        var linked = Path.Combine(EffectAuthorityRoot(paths), "linked-evidence.json");
        if (!CapabilityCatalogUnixFifo.TryCreateHardLink(PrimaryPath(paths), linked))
        {
            return;
        }

        var result = await Store(paths, trust)
            .ReserveAsync(Usage("target-two", "target-operation-two", runId: "run-2"));

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, result.Status);
        Assert.DoesNotContain("target-operation-two", await File.ReadAllTextAsync(linked), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copied_evidence_and_symlinked_paths_never_gain_authority()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            (await Store(sourcePaths, trust).AppendAsync(Decision("effect-operation-one"))).Status);
        CopyDirectory(sourcePaths.AgentPath, destinationPaths.AgentPath);

        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(destinationPaths, trust).AppendAsync(Decision("effect-operation-one"))).Status);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var linked = new TestWorkspace();
        using var outside = new TestWorkspace();
        var linkedPaths = new WorkspacePaths(linked.RootPath);
        Directory.CreateDirectory(linkedPaths.AgentPath);
        Directory.CreateSymbolicLink(Path.Combine(linkedPaths.AgentPath, "loops"), outside.RootPath);
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(linkedPaths, new TestCapabilityLifecycleTrustProvider()).AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Shared_authority_transaction_is_reentrant_and_release_failure_preserves_completed_result()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var transaction = new CapabilityAuthorityTransaction(paths);
        var decision = Decision("effect-operation-one");
        var nested = new GovernedLoopEffectAuthorityEvidenceStore(paths, trust, authorityTransaction: transaction);

        var appended = await transaction.ExecuteAsync(token => nested.AppendAsync(decision, token));
        var releasing = new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            trust,
            authorityTransaction: new ThrowAfterEffectAuthorityCallbackTransaction());
        var replayed = await releasing.AppendAsync(decision);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, appended.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable, GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)]
    public async Task Durable_boundary_failure_returns_conservative_posture_and_exact_retry_recovers(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        GovernedLoopEffectAuthorityEvidenceStoreStatus interruptedStatus,
        GovernedLoopEffectAuthorityEvidenceStoreStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var decision = Decision("effect-operation-one");
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

        var interrupted = await Store(paths, trust, options).AppendAsync(decision);
        var retried = await Store(paths, trust).AppendAsync(decision);
        var replayed = await Store(paths, trust).AppendAsync(decision);

        Assert.Equal(interruptedStatus, interrupted.Status);
        Assert.Equal(retryStatus, retried.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Fact]
    public async Task Cancellation_propagates_before_durable_intent_and_becomes_ambiguous_after_proof()
    {
        using var beforeWorkspace = new TestWorkspace();
        var beforePaths = new WorkspacePaths(beforeWorkspace.RootPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(
                beforePaths,
                new TestCapabilityLifecycleTrustProvider())
            .AppendAsync(Decision("effect-operation-one"), new CancellationToken(canceled: true)));
        Assert.False(File.Exists(PrimaryPath(beforePaths)));

        using var afterWorkspace = new TestWorkspace();
        var afterPaths = new WorkspacePaths(afterWorkspace.RootPath);
        var cancellation = new CancellationTokenSource();
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            }
        };
        var result = await Store(afterPaths, new TestCapabilityLifecycleTrustProvider(), options)
            .AppendAsync(Decision("effect-operation-one"), cancellation.Token);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, result.Status);
        Assert.Null(result.ContentHash);
    }

    [Theory]
    [InlineData("crash-proof")]
    [InlineData("crash-primary")]
    [InlineData("crash-trust")]
    public async Task Abrupt_process_loss_at_each_durable_boundary_recovers_exactly_once(string mode)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = workspace.File("gate");
        var ready = workspace.File("ready");
        using var process = StartCrossProcessHost(
            mode,
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            ready,
            "effect-operation-one");
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);

        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var decision = Decision("effect-operation-one");
        var recovered = await Store(paths, trust).AppendAsync(decision);
        var replayed = await Store(paths, new FileCapabilityCatalogTrustProvider(trustRoot.RootPath)).AppendAsync(decision);

        Assert.Equal(
            mode == "crash-proof"
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
            recovered.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Fact]
    public void Constructor_rejects_invalid_bounds_and_overlapping_trust()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectAuthorityEvidenceStore(null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectAuthorityEvidenceStore(paths, (ICapabilityCatalogTrustProvider)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxDecisions = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxTargetReservations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxCompletionClaims = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions
            {
                MaxArtifactUtf8Bytes = GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes + 1
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(0)));
        Assert.Throws<InvalidOperationException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new FileCapabilityCatalogTrustProvider(Path.Combine(paths.AgentPath, "server-trust"))));
        Assert.NotNull(new GovernedLoopEffectAuthorityEvidenceStore(paths));
    }

    private static GovernedLoopEffectAuthorityEvidenceStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        => new(paths, trust, options, authorityTransaction: authorityTransaction);

    private static GovernedLoopEffectAuthorityDecision Decision(string effectOperationId)
    {
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision();
        return Rehash(decision with { EffectOperationId = effectOperationId });
    }

    private static GovernedLoopEffectAuthorityDecision Rehash(GovernedLoopEffectAuthorityDecision decision)
        => GovernedLoopEffectAuthorityContractHash.Apply(decision with { ContentHash = string.Empty });

    private static GovernedLoopEffectAuthorityUsageRequest Usage(
        string targetSeed,
        string effectOperationId,
        string runId = "run-1",
        long executionGeneration = 1,
        string nodeId = "inference-1",
        int nodeAttempt = 1,
        GovernedLoopEffectBoundaryKind boundaryKind = GovernedLoopEffectBoundaryKind.WorkspaceToolIntake)
    {
        var decision = Decision("usage-fixture");
        return new GovernedLoopEffectAuthorityUsageRequest(
            GovernedLoopEffectAuthorityUsageRequest.CurrentSchemaVersion,
            decision.AdmittedAuthority.Grant,
            AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion,
            decision.AdmissionReceiptHash,
            runId,
            executionGeneration,
            nodeId,
            nodeAttempt,
            effectOperationId,
            boundaryKind,
            1,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetSeed))).ToLowerInvariant(),
            GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc);
    }

    private static GovernedLoopEffectAuthorityCompletionUsageRequest Completion(long executionGeneration = 1)
    {
        var decision = Decision("completion-fixture");
        return new GovernedLoopEffectAuthorityCompletionUsageRequest(
            GovernedLoopEffectAuthorityCompletionUsageRequest.CurrentSchemaVersion,
            decision.AdmittedAuthority.Grant,
            decision.AdmissionReceiptHash,
            decision.RunId,
            executionGeneration,
            "run-completion-one",
            GovernedLoopEffectAuthorityTestFixture.EvaluatedAtUtc);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return value[..index] + newValue + value[(index + oldValue.Length)..];
    }

    private static GovernedLoopEffectAuthorityEvidenceStoreOptions FailingAt(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary)
        => new()
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask,
        };

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "effect-authority", "decisions.json");

    private static string ProofPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "effect-authority", "decisions.proved.json");

    private static string LockPath(WorkspacePaths paths)
        => Path.Combine(EffectAuthorityRoot(paths), ".mutations.lock");

    private static string EffectAuthorityRoot(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "effect-authority");

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static Process StartCrossProcessHost(
        string mode,
        string workspace,
        string trustRoot,
        string gate,
        string ready,
        string operation)
        => CancellationHostProcess.Start(
            "effect-authority-crash",
            mode,
            workspace,
            trustRoot,
            gate,
            ready,
            operation);

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process effect-authority store host did not publish `{path}`.");
            await Task.Delay(10);
        }
    }

    public enum AdversarialTrustBehavior
    {
        MismatchedInitialization,
        EmptyAuthenticationTag,
        MismatchedSuccessor,
        WrongWorkspaceRead,
    }

    private sealed class AdversarialEffectAuthorityTrustProvider(
        ICapabilityCatalogTrustProvider inner,
        AdversarialTrustBehavior behavior) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath)
            => inner.RequireDisjointWorkspace(workspaceRootPath);

        public async Task<CapabilityCatalogTrustState?> ReadAsync(
            string workspaceIdentity,
            CancellationToken cancellationToken = default)
        {
            if (behavior == AdversarialTrustBehavior.WrongWorkspaceRead)
            {
                return new CapabilityCatalogTrustState(
                    "wrong-workspace",
                    0,
                    Hash("wrong-current-digest"),
                    null,
                    null);
            }

            return await inner.ReadAsync(workspaceIdentity, cancellationToken);
        }

        public async Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
        {
            var initialized = await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
            return behavior == AdversarialTrustBehavior.MismatchedInitialization
                ? initialized with { CurrentContentDigest = Hash("wrong-initialized-digest") }
                : initialized;
        }

        public async Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => behavior == AdversarialTrustBehavior.EmptyAuthenticationTag
                ? string.Empty
                : await inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            string authenticationTag,
            CancellationToken cancellationToken = default)
            => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public async Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
        {
            var advanced = await inner.AdvanceAsync(
                workspaceIdentity,
                expectedGeneration,
                expectedContentDigest,
                newGeneration,
                newContentDigest,
                cancellationToken);
            return behavior == AdversarialTrustBehavior.MismatchedSuccessor
                ? advanced with { CurrentContentDigest = Hash("wrong-successor-digest") }
                : advanced;
        }
    }

    private sealed class ThrowAfterEffectAuthorityFileOpenObserver : ICapabilityCatalogPathObserver
    {
        public void BeforeDirectoryChildOpen(string parentPath, string childName)
        {
        }

        public void BeforeFileChildOpen(string parentPath, string childName)
        {
        }

        public void AfterFileChildOpen(string parentPath, string childName)
            => throw new IOException("Injected retained-handle read observer failure.");
    }
}
