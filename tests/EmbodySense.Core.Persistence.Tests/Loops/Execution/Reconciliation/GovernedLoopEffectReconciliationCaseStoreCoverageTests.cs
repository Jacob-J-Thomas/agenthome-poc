using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationCaseStoreCoverageTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _journalJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public async Task Empty_store_operations_are_safe_and_recoverable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectReconciliationCaseStore(paths);

        Assert.True(await store.ProbeStorageAvailabilityAsync());
        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, page.Status);
        Assert.Empty(page.Cases);
        var read = await ((IGovernedLoopEffectReconciliationCaseStore)store).ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(new("missing-case", 1, Hash('a'), Hash('b'))));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, read.Status);
        Assert.True(await store.RecoverAsync());
    }

    [Fact]
    public async Task Resolution_reads_and_cursor_validation_fail_closed_for_bad_artifacts()
    {
        using (var emptyWorkspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(emptyWorkspace.RootPath);
            var emptyStore = new GovernedLoopEffectReconciliationCaseStore(paths);
            var execution = GovernedLoopExecutionBinding.Create(1, "run-1", GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('a')), 1);
            var binding = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationBinding(
                1, CapabilityWorkspaceScopeId.Create(paths.RootPath), execution, "node", 0, 1, 1, "effect", "operation", 1, Hash('b'), Hash('c'), Hash('d')));
            var reference = new GovernedLoopEffectReconciliationCaseReference("missing-case", 1, Hash('d'), binding.ContentHash);
            var resolution = await ((IGovernedLoopEffectReconciliationResolutionReader)emptyStore).ReadAsync(new(reference, binding));
            Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, resolution.Status);
        }

        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
        Assert.True(await store.ProbeStorageAvailabilityAsync());
        var resolutionReader = (IGovernedLoopEffectReconciliationResolutionReader)store;
        var caseReader = (IGovernedLoopEffectReconciliationCaseStore)store;
        var resolutionRequest = new GovernedLoopEffectReconciliationResolutionReadRequest(Reference(scenario.Open), scenario.Open.Binding);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, (await resolutionReader.ReadAsync(resolutionRequest)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, (await caseReader.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(new(scenario.Open.CaseId, scenario.Open.CaseVersion, Hash('f'), scenario.Open.Binding.ContentHash)))).Status);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolutionReader.ReadAsync(resolutionRequest, cancellation.Token));
        }

        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, new string('x', 1024)))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, "not-base64"))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, Convert.ToBase64String(Encoding.UTF8.GetBytes("v2\ncase-1"))))).Status);
        var checksumMismatchCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("v1\ncase-1")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "x";
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, checksumMismatchCursor))).Status);
        var invalidCandidateCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("v1\nnot a valid id")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, invalidCandidateCursor))).Status);
        var paddedCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("v1\ncase-12")).Replace('+', '-').Replace('/', '_');
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Invalid, (await store.ListAsync(new(10, paddedCursor))).Status);

        var lockPath = Path.Combine(scenario.Paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock");
        await using (var externalLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var readCancellation = new CancellationTokenSource();
            var pendingRead = caseReader.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(Reference(scenario.Open)), readCancellation.Token);
            readCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
            using var resolutionCancellation = new CancellationTokenSource();
            var pendingResolution = resolutionReader.ReadAsync(resolutionRequest, resolutionCancellation.Token);
            resolutionCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingResolution);
            using var listCancellation = new CancellationTokenSource();
            var pendingList = store.ListAsync(new(10), listCancellation.Token);
            listCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingList);
            using var mutationCancellation = new CancellationTokenSource();
            var pendingMutation = store.CompareExchangeAsync(Mutation(scenario.Open, "cancelled"), mutationCancellation.Token);
            mutationCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingMutation);
            Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, (await resolutionReader.ReadAsync(resolutionRequest)).Status);
        }

        File.Delete(Path.Combine(scenario.Paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"));
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, (await resolutionReader.ReadAsync(resolutionRequest)).Status);
    }

    [Fact]
    public async Task Recovery_repairs_a_missing_case_head_and_rejects_journal_boundaries()
    {
        using (var repairWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(repairWorkspace);
            var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
            var assessed = Assessed(scenario.Open);
            var pending = PendingStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await pending.CompareExchangeAsync(Mutation(assessed, "recover-head", scenario.Attempt.Payload.OperationId, 1, scenario.Open.ContentHash))).Status);
            var headPath = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.head").Single();
            File.Delete(headPath);
            Assert.True(await store.RecoverAsync());
            Assert.True(File.Exists(headPath));
        }

        using (var conflictingWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(conflictingWorkspace);
            var pending = PendingStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await pending.CompareExchangeAsync(Mutation(scenario.Open, "repair-binding", scenario.Attempt.Payload.OperationId))).Status);
            await RewriteJournalAsync(JournalPath(scenario.Paths, scenario.Attempt.Payload.OperationId), _ => { }, replacementHash: Hash('f'));
            var repairedStore = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.False(await repairedStore.RecoverAsync());
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.RepairRequired, (await repairedStore.CompareExchangeAsync(Mutation(scenario.Open, "repair-binding", scenario.Attempt.Payload.OperationId))).Status);
        }

        using (var rootWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(rootWorkspace);
            var pending = PendingStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await pending.CompareExchangeAsync(Mutation(scenario.Open, "repair-root", scenario.Attempt.Payload.OperationId))).Status);
            var assessed = Assessed(scenario.Open);
            await RewriteJournalAsync(JournalPath(scenario.Paths, scenario.Attempt.Payload.OperationId), _ => { }, Convert.ToBase64String(GovernedLoopEffectReconciliationRecordCodec.Encode(assessed)), assessed.ContentHash, assessed.CaseVersion);
            Assert.False(await new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore).RecoverAsync());
        }

        using var transitionWorkspace = new TestWorkspace();
        var transitionScenario = await CreateScenarioAsync(transitionWorkspace);
        var transitionStore = new GovernedLoopEffectReconciliationCaseStore(transitionScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await transitionStore.CompareExchangeAsync(Mutation(transitionScenario.Open, "open"))).Status);
        var transitionAssessed = Assessed(transitionScenario.Open);
        var transitionPending = PendingStore(transitionScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await transitionPending.CompareExchangeAsync(Mutation(transitionAssessed, "repair-transition", transitionScenario.Attempt.Payload.OperationId, 1, transitionScenario.Open.ContentHash))).Status);
        var altered = GovernedLoopEffectReconciliationContractHash.Apply(transitionAssessed with { OpenedAtUtc = transitionAssessed.OpenedAtUtc.AddSeconds(1), ContentHash = string.Empty });
        await RewriteJournalAsync(JournalPath(transitionScenario.Paths, transitionScenario.Attempt.Payload.OperationId), _ => { }, Convert.ToBase64String(GovernedLoopEffectReconciliationRecordCodec.Encode(altered)), altered.ContentHash, altered.CaseVersion);
        Assert.False(await transitionStore.RecoverAsync());

        using var journalLimitWorkspace = new TestWorkspace();
        var journalLimitScenario = await CreateScenarioAsync(journalLimitWorkspace);
        var journalLimitStore = new GovernedLoopEffectReconciliationCaseStore(journalLimitScenario.EffectStore);
        for (var index = 0; index < 65; index++)
        {
            var key = index.ToString("x").PadLeft(64, '0');
            await File.WriteAllTextAsync(Path.Combine(journalLimitScenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-journal." + key + ".json"), "{}");
        }
        Assert.False(await journalLimitStore.RecoverAsync());
    }

    [Fact]
    public async Task Populated_read_surfaces_fail_closed_for_lock_contention_and_cancellation()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
        var lockPath = Path.Combine(scenario.Paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock");

        await using (var externalLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await store.ProbeStorageAvailabilityAsync());
            Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, (await store.ListAsync(new(10))).Status);
            Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, (await ((IGovernedLoopEffectReconciliationCaseStore)store).ReadAsync(new(Reference(scenario.Open)))).Status);

            using var cancellation = new CancellationTokenSource();
            var pending = store.ProbeStorageAvailabilityAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RecoverAsync(cancellation.Token));
        }

        await using var unavailableLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(await store.RecoverAsync());
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await store.CompareExchangeAsync(Mutation(scenario.Open, "unavailable"))).Status);
    }

    [Fact]
    public async Task Valid_pending_create_journal_is_observed_without_replaying_or_mutating_it()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var operationId = scenario.Attempt.Payload.OperationId;
        var pendingStore = PendingStore(scenario.EffectStore);
        var request = Mutation(scenario.Open, "pending", operationId);

        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await pendingStore.CompareExchangeAsync(request)).Status);
        var journalPath = JournalPath(scenario.Paths, operationId);
        Assert.True(File.Exists(journalPath));

        var page = await pendingStore.ListAsync(new(10));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, page.Status);
        Assert.False(await pendingStore.ProbeStorageAvailabilityAsync());
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, (await ((IGovernedLoopEffectReconciliationCaseStore)pendingStore).ReadAsync(new(Reference(scenario.Open)))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, (await ((IGovernedLoopEffectReconciliationResolutionReader)pendingStore).ReadAsync(new(Reference(scenario.Open), scenario.Open.Binding))).Status);
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public async Task Pending_journal_payload_and_stage_mismatches_are_rejected_fail_closed()
    {
        await AssertPendingJournalVariantAsync((journal, _) => journal["replacementHash"] = Hash('f'));
        await AssertPendingJournalVariantAsync((journal, _) => journal["replacementVersion"] = 2);
        await AssertPendingJournalVariantAsync((journal, _) => journal["expectedEffectHash"] = Hash('f'));
        await AssertPendingJournalVariantAsync((journal, _) => journal["expectedCaseHash"] = Hash('f'));
        await AssertPendingJournalVariantAsync((journal, _) => journal["stage"] = "effectPublished");
        await AssertPendingJournalVariantAsync((journal, _) =>
        {
            journal["caseId"] = "different-case";
            journal["storageKey"] = CaseStorageKey("different-case");
        });

        using var updateWorkspace = new TestWorkspace();
        var updateScenario = await CreateScenarioAsync(updateWorkspace);
        var assessed = Assessed(updateScenario.Open);
        var updateBaselineStore = new GovernedLoopEffectReconciliationCaseStore(updateScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await updateBaselineStore.CompareExchangeAsync(Mutation(updateScenario.Open, "open"))).Status);
        var updateStore = PendingStore(updateScenario.EffectStore);
        var updateRequest = Mutation(assessed, "pending-update", updateScenario.Attempt.Payload.OperationId, updateScenario.Open.CaseVersion, updateScenario.Open.ContentHash);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await updateStore.CompareExchangeAsync(updateRequest)).Status);
        var updateJournal = JournalPath(updateScenario.Paths, updateScenario.Attempt.Payload.OperationId);
        await RewriteJournalAsync(updateJournal, journal => journal["expectedCaseHash"] = null, Convert.ToBase64String(GovernedLoopEffectReconciliationRecordCodec.Encode(assessed)), assessed.ContentHash, assessed.CaseVersion);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await updateStore.ListAsync(new(10))).Status);

        using var successorWorkspace = new TestWorkspace();
        var successorScenario = await CreateScenarioAsync(successorWorkspace);
        var resolved = Resolved(successorScenario.Open, successorScenario.Attempt);
        var successorBaselineStore = new GovernedLoopEffectReconciliationCaseStore(successorScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await successorBaselineStore.CompareExchangeAsync(Mutation(successorScenario.Open, "open"))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await successorBaselineStore.CompareExchangeAsync(Mutation(resolved.Assessed, "assess", expectedVersion: 1, expectedHash: successorScenario.Open.ContentHash))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await successorBaselineStore.CompareExchangeAsync(Mutation(resolved.Disposed, "dispose", expectedVersion: 2, expectedHash: resolved.Assessed.ContentHash))).Status);
        var successorStore = PendingStore(successorScenario.EffectStore);
        var successorRequest = Mutation(resolved.Case, "pending-successor", successorScenario.Attempt.Payload.OperationId, resolved.Disposed.CaseVersion, resolved.Disposed.ContentHash, resolved.Successor);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await successorStore.CompareExchangeAsync(successorRequest)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, (await successorStore.ListAsync(new(10))).Status);
        await RewriteJournalAsync(JournalPath(successorScenario.Paths, successorScenario.Attempt.Payload.OperationId), journal => journal["successorJson"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await successorStore.ListAsync(new(10))).Status);

        using var malformedWorkspace = new TestWorkspace();
        var malformedScenario = await CreateScenarioAsync(malformedWorkspace);
        var malformedStore = PendingStore(malformedScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await malformedStore.CompareExchangeAsync(Mutation(malformedScenario.Open, "pending-decode", malformedScenario.Attempt.Payload.OperationId))).Status);
        await RewriteJournalAsync(JournalPath(malformedScenario.Paths, malformedScenario.Attempt.Payload.OperationId), _ => { }, replacementJson: Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await malformedStore.ListAsync(new(10))).Status);
    }

    [Fact]
    public async Task Case_inventory_and_receipt_integrity_fail_closed_without_repairing_evidence()
    {
        using (var noVersionWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(noVersionWorkspace);
            var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
            var caseVersion = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.json").Single();
            File.Delete(caseVersion);
            Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await store.ListAsync(new(10))).Status);
        }

        using (var noHeadWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(noHeadWorkspace);
            var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
            var caseHead = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-case.*.head").Single();
            File.Delete(caseHead);
            Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await store.ListAsync(new(10))).Status);
            Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Corrupt, (await ((IGovernedLoopEffectReconciliationCaseStore)store).ReadAsync(new(Reference(scenario.Open)))).Status);
        }

        using var receiptWorkspace = new TestWorkspace();
        var receiptScenario = await CreateScenarioAsync(receiptWorkspace);
        var receiptStore = new GovernedLoopEffectReconciliationCaseStore(receiptScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await receiptStore.CompareExchangeAsync(Mutation(receiptScenario.Open, "open"))).Status);
        var receiptPath = Directory.EnumerateFiles(receiptScenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-receipt.*.json").Single();
        await RewriteReceiptAsync(receiptPath, receipt => receipt["caseContentHash"] = Hash('f'));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await receiptStore.ListAsync(new(10))).Status);
    }

    [Fact]
    public async Task Receipt_inventory_rejects_orphans_wrong_case_versions_and_unrelated_effects()
    {
        using (var orphanWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(orphanWorkspace);
            var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
            var receiptPath = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-receipt.*.json").Single();
            await RewriteReceiptAsync(receiptPath, receipt => receipt["caseId"] = "orphan-case");
            Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await store.ListAsync(new(10))).Status);
        }

        using (var wrongCaseWorkspace = new TestWorkspace())
        {
            var scenario = await CreateScenarioAsync(wrongCaseWorkspace);
            var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
            Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);
            var receiptPath = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-receipt.*.json").Single();
            await RewriteReceiptAsync(receiptPath, receipt => receipt["caseContentHash"] = Hash('f'));
            Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await store.ListAsync(new(10))).Status);
        }

        using var unrelatedWorkspace = new TestWorkspace();
        var unrelatedScenario = await CreateScenarioAsync(unrelatedWorkspace);
        var unrelatedStore = new GovernedLoopEffectReconciliationCaseStore(unrelatedScenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await unrelatedStore.CompareExchangeAsync(Mutation(unrelatedScenario.Open, "open"))).Status);
        var unrelatedReceiptPath = Directory.EnumerateFiles(unrelatedScenario.Paths.GovernedLoopEffectAttemptsPath, "reconciliation-receipt.*.json").Single();
        await RewriteReceiptAsync(unrelatedReceiptPath, receipt => receipt["effectContentHash"] = FindPriorEffectHash(unrelatedScenario));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await unrelatedStore.ListAsync(new(10))).Status);
    }

    [Fact]
    public async Task Invalid_mutations_cover_binding_effect_transition_and_capacity_guards()
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var store = new GovernedLoopEffectReconciliationCaseStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, (await store.CompareExchangeAsync(Mutation(scenario.Open, "open"))).Status);

        var foreignBinding = GovernedLoopEffectReconciliationContractHash.Apply(scenario.Open.Binding with { WorkspaceId = CapabilityWorkspaceScopeId.Create(Path.Combine(workspace.RootPath, "foreign-workspace")), ContentHash = string.Empty });
        var foreignCase = GovernedLoopEffectReconciliationContract.Open("foreign-case", foreignBinding, scenario.Open.ContractMetadata, [], [], _now);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid, (await store.CompareExchangeAsync(Mutation(foreignCase, "foreign"))).Status);

        var mismatchedBinding = GovernedLoopEffectReconciliationContractHash.Apply(scenario.Open.Binding with { CurrentAttemptHash = Hash('f'), ContentHash = string.Empty });
        var mismatchedCase = GovernedLoopEffectReconciliationContract.Open("mismatched-case", mismatchedBinding, scenario.Open.ContractMetadata, [], [], _now);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid, (await store.CompareExchangeAsync(Mutation(mismatchedCase, "mismatch"))).Status);

        var missingEffectBinding = GovernedLoopEffectReconciliationContractHash.Apply(scenario.Open.Binding with { OperationId = "missing-operation", ContentHash = string.Empty });
        var missingEffectCase = GovernedLoopEffectReconciliationContract.Open("missing-effect-case", missingEffectBinding, scenario.Open.ContractMetadata, [], [], _now);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await store.CompareExchangeAsync(Mutation(missingEffectCase, "missing-effect"))).Status);

        var invalidTransition = GovernedLoopEffectReconciliationContract.Create(
            scenario.Open.CaseId,
            2,
            scenario.Open.Binding,
            scenario.Open.ContractMetadata,
            [],
            [],
            [],
            null,
            null,
            null,
            [],
            scenario.Open.ContentHash,
            scenario.Open.OpenedAtUtc.AddSeconds(1),
            scenario.Open.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid, (await store.CompareExchangeAsync(Mutation(invalidTransition, "transition", expectedVersion: scenario.Open.CaseVersion, expectedHash: scenario.Open.ContentHash))).Status);

        var assessed = Assessed(scenario.Open);
        var retainedFiles = Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath).Select(path => new FileInfo(path).Length).ToArray();
        var constrainedEffectStore = new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspace.RootPath), new GovernedLoopEffectAttemptStoreOptions
        {
            MaxRecordUtf8Bytes = Math.Max(checked((int)retainedFiles.Max()), GovernedLoopEffectReconciliationRecordCodec.Encode(assessed).Length),
            MaxStoreUtf8Bytes = retainedFiles.Sum()
        });
        var constrainedStore = new GovernedLoopEffectReconciliationCaseStore(constrainedEffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded, (await constrainedStore.CompareExchangeAsync(Mutation(assessed, "capacity", scenario.Attempt.Payload.OperationId, 1, scenario.Open.ContentHash))).Status);
    }

    private static GovernedLoopEffectReconciliationCaseStore PendingStore(GovernedLoopEffectAttemptStore effectStore)
        => new(effectStore, TimeProvider.System, new GovernedLoopEffectReconciliationCaseStoreOptions
        {
            DurableBoundaryObserver = _ => throw new InvalidOperationException("simulated process loss")
        });

    private static async Task AssertPendingJournalVariantAsync(Action<JsonObject, Scenario> mutate)
    {
        using var workspace = new TestWorkspace();
        var scenario = await CreateScenarioAsync(workspace);
        var operationId = scenario.Attempt.Payload.OperationId;
        var store = PendingStore(scenario.EffectStore);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, (await store.CompareExchangeAsync(Mutation(scenario.Open, "pending", operationId))).Status);
        var journalPath = JournalPath(scenario.Paths, operationId);
        await RewriteJournalAsync(journalPath, journal => mutate(journal, scenario));
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, (await store.ListAsync(new(10))).Status);
        Assert.False(await store.ProbeStorageAvailabilityAsync());
    }

    private static async Task RewriteJournalAsync(
        string path,
        Action<JsonObject> mutate,
        string? replacementJson = null,
        string? replacementHash = null,
        long? replacementVersion = null)
    {
        var originalJson = await File.ReadAllTextAsync(path);
        var original = JsonNode.Parse(originalJson)!.AsObject();
        var journal = original.DeepClone().AsObject();
        if (replacementJson is not null)
        {
            journal["replacementJson"] = replacementJson;
        }
        if (replacementHash is not null)
        {
            journal["replacementHash"] = replacementHash;
        }
        if (replacementVersion is not null)
        {
            journal["replacementVersion"] = replacementVersion;
        }
        mutate(journal);
        var rewritten = ReplaceChangedJsonProperties(originalJson, original, journal);
        rewritten = ReplaceJsonProperty(rewritten, "contentHash", "\"\"");
        var unsigned = Encoding.UTF8.GetBytes(rewritten);
        var hashMaterial = Encoding.UTF8.GetBytes("embodysense.governed-loop-effect-reconciliation-persistence.v1\njournal\n" + Convert.ToBase64String(unsigned));
        rewritten = ReplaceJsonProperty(rewritten, "contentHash", JsonSerializer.Serialize(Convert.ToHexString(SHA256.HashData(hashMaterial)).ToLowerInvariant(), _journalJson));
        await File.WriteAllTextAsync(path, rewritten);
    }

    private static async Task RewriteReceiptAsync(string path, Action<JsonObject> mutate)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };
        var originalJson = await File.ReadAllTextAsync(path);
        var original = JsonNode.Parse(originalJson)!.AsObject();
        var receipt = original.DeepClone().AsObject();
        mutate(receipt);
        var rewritten = ReplaceChangedJsonProperties(originalJson, original, receipt);
        rewritten = ReplaceJsonProperty(rewritten, "contentHash", "\"\"");
        var unsigned = Encoding.UTF8.GetBytes(rewritten);
        var hashMaterial = Encoding.UTF8.GetBytes("embodysense.governed-loop-effect-reconciliation-persistence.v1\nreceipt\n" + Convert.ToBase64String(unsigned));
        rewritten = ReplaceJsonProperty(rewritten, "contentHash", JsonSerializer.Serialize(Convert.ToHexString(SHA256.HashData(hashMaterial)).ToLowerInvariant(), json));
        await File.WriteAllTextAsync(path, rewritten);
    }

    private static string ReplaceChangedJsonProperties(string originalJson, JsonObject original, JsonObject updated)
    {
        var rewritten = originalJson;
        foreach (var property in updated)
        {
            if (!JsonNode.DeepEquals(original[property.Key], property.Value))
            {
                rewritten = ReplaceJsonProperty(rewritten, property.Key, SerializeJsonNode(property.Value));
            }
        }
        return rewritten;
    }

    private static string ReplaceJsonProperty(string json, string propertyName, string serializedValue)
    {
        var pattern = $"(\\\"{Regex.Escape(propertyName)}\\\":)(?:null|\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|-?\\d+)";
        return new Regex(pattern, RegexOptions.CultureInvariant).Replace(json, match => match.Groups[1].Value + serializedValue, 1);
    }

    private static string SerializeJsonNode(JsonNode? value)
    {
        if (value is null)
        {
            return "null";
        }
        try
        {
            return JsonSerializer.Serialize(value.GetValue<string>(), _journalJson);
        }
        catch (InvalidOperationException)
        {
            return value.ToJsonString(_journalJson);
        }
    }

    private static async Task<Scenario> CreateScenarioAsync(TestWorkspace workspace)
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var prepared = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var paths = new WorkspacePaths(workspace.RootPath);
        var effectStore = new GovernedLoopEffectAttemptStore(paths);
        var begun = await effectStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, begun.Status);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('6'), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, _now.AddSeconds(2));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, _now.AddSeconds(3));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(prepared.ContentHash, authorized, begun.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(authorized.ContentHash, crossed, begun.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(crossed.ContentHash, attempt, begun.Lease!)).Status);
        begun.Lease!.Dispose();
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(workspaceId, 1, 1, attempt);
        var metadata = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "contract-1",
            1,
            fixture.Request.CapabilityPin.DescriptorIdentity,
            fixture.Request.CapabilityPin.Implementation,
            fixture.Descriptor.OperationId,
            fixture.Descriptor.ContentHash,
            "probe-1",
            1,
            Hash('7'),
            string.Empty));
        var open = GovernedLoopEffectReconciliationContract.Open("case-1", binding, metadata, [], [], _now.AddSeconds(4));
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(open, attempt).IsValid);
        return new Scenario(paths, effectStore, workspaceId, attempt, open);
    }

    private static GovernedLoopEffectReconciliationCase Assessed(GovernedLoopEffectReconciliationCase open)
    {
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(1, open.CaseId, open.Binding.ContentHash, "assessment-1", GovernedLoopEffectReconciliationAssessmentKind.Inconclusive, [], Hash('a'), open.UpdatedAtUtc.AddSeconds(1), "Awaiting conclusive evidence.", string.Empty));
        return GovernedLoopEffectReconciliationContract.Create(open.CaseId, 2, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, [assessment], assessment.ContentHash, null, null, open.CaseReceiptHashes, open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
    }

    private static ResolvedScenario Resolved(GovernedLoopEffectReconciliationCase open, GovernedLoopEffectAttempt current)
    {
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(1, open.CaseId, open.Binding.ContentHash, "source-1", GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative, GovernedLoopEffectReconciliationReliabilityPosture.Authoritative, open.ContractMetadata.ContractId, open.ContractMetadata.ContractVersion, open.ContractMetadata.ContentHash, Hash('1'), open.OpenedAtUtc, null, string.Empty));
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(1, open.CaseId, open.Binding.ContentHash, "observation-1", source.SourceId, source.ContentHash, GovernedLoopEffectReconciliationObservationKind.Evidence, source.ReliabilityPosture, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, "evidence-1", Hash('2'), open.OpenedAtUtc.AddSeconds(1), open.OpenedAtUtc.AddSeconds(2), "No matching external effect exists.", string.Empty));
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(1, open.CaseId, open.Binding.ContentHash, "assessment-accepted", GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, [observation.ContentHash], Hash('3'), open.OpenedAtUtc.AddSeconds(3), "Authoritative evidence proves absence.", string.Empty));
        var assessed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 2, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, null, null, [], open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
        var disposition = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(1, open.CaseId, open.Binding.ContentHash, "disposition-1", GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, assessment.ContentHash, Hash('4'), open.OpenedAtUtc.AddSeconds(4), "Accept exact absence proof.", string.Empty));
        var disposed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 3, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, disposition, null, [], assessed.ContentHash, open.OpenedAtUtc, disposition.DisposedAtUtc);
        var resolution = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(1, open.CaseId, open.Binding.ContentHash, "resolution-1", assessment.ContentHash, disposition.ContentHash, GovernedLoopEffectOutcome.NotApplied, null, null, Hash('5'), open.OpenedAtUtc.AddSeconds(5), "Resolve as not applied.", string.Empty));
        var resolved = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 4, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, disposition, resolution, [], disposed.ContentHash, open.OpenedAtUtc, resolution.ResolvedAtUtc);
        return new ResolvedScenario(resolved, assessed, disposed, GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(current, resolved));
    }

    private static GovernedLoopEffectReconciliationCaseMutationRequest Mutation(GovernedLoopEffectReconciliationCase replacement, string purpose, string? operationId = null, long? expectedVersion = null, string? expectedHash = null, GovernedLoopEffectAttempt? successor = null)
        => new(operationId ?? $"mutation-{purpose}", PersistenceHash("request", operationId ?? $"mutation-{purpose}"), purpose, expectedVersion, expectedHash, replacement.Binding, replacement, successor);

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static string JournalPath(WorkspacePaths paths, string operationId)
        => Path.Combine(paths.GovernedLoopEffectAttemptsPath, "reconciliation-journal." + OperationKey(operationId) + ".json");

    private static string CaseStorageKey(string caseId)
        => PersistenceHash("case", caseId);

    private static string OperationKey(string operationId)
        => PersistenceHash("operation", operationId);

    private static string PersistenceHash(string domain, string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-reconciliation-persistence.v1\n{domain}\n{value}"))).ToLowerInvariant();

    private static string FindPriorEffectHash(Scenario scenario)
    {
        foreach (var path in Directory.EnumerateFiles(scenario.Paths.GovernedLoopEffectAttemptsPath, "*.json"))
        {
            if (GovernedLoopEffectAttemptRecordCodec.TryDecode(File.ReadAllBytes(path), out var candidate, out _)
                && candidate is not null
                && string.Equals(candidate.Payload.OperationId, scenario.Attempt.Payload.OperationId, StringComparison.Ordinal)
                && !string.Equals(candidate.ContentHash, scenario.Attempt.ContentHash, StringComparison.Ordinal))
            {
                return candidate.ContentHash;
            }
        }

        throw new InvalidOperationException("The scenario did not retain an earlier immutable effect version.");
    }

    private static string Hash(char value) => GovernedLoopEffectAttemptTestFixture.Hash(value);

    private sealed record Scenario(WorkspacePaths Paths, GovernedLoopEffectAttemptStore EffectStore, string WorkspaceId, GovernedLoopEffectAttempt Attempt, GovernedLoopEffectReconciliationCase Open);

    private sealed record ResolvedScenario(GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectReconciliationCase Assessed, GovernedLoopEffectReconciliationCase Disposed, GovernedLoopEffectAttempt Successor);
}
