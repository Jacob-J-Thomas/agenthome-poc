using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopRuntimeTests
{
    private static readonly TimeSpan _providerAttemptStartTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Context_capture_truncates_role_and_conversation_sources_only_at_valid_utf16_boundaries()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var roleLabel = $"[EmbodySense role instruction source: .agent/ROLE.md]{Environment.NewLine}";
        var roleMarker = $"{Environment.NewLine}[source truncated to fit the 12000-character admitted source limit]";
        var rolePrefixCharacters = 12_000 - roleLabel.Length - roleMarker.Length;
        await File.WriteAllTextAsync(workspace.File(".agent", "ROLE.md"), new string('r', rolePrefixCharacters - 1) + "😀" + new string('r', 500));
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-unicode-context", "update-runtime-unicode-context");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var conversationMarker = $"{Environment.NewLine}[truncated to {CustomLoopLimits.MaxInvokingConversationCharacters} characters for invoking-conversation admission]{Environment.NewLine}";
        var availableConversationCharacters = CustomLoopLimits.MaxInvokingConversationCharacters - conversationMarker.Length;
        var conversationHeadCharacters = availableConversationCharacters / 2;
        var conversationTailCharacters = availableConversationCharacters - conversationHeadCharacters;
        var formattedPrefix = $"[EmbodySense untrusted logical conversation assistant source: message 2]{Environment.NewLine}fake response: ";
        var promptPrefixCharacters = conversationHeadCharacters - formattedPrefix.Length - 1;
        const int TailBoundaryOffset = 100;
        var oversizedPrompt = new string('p', promptPrefixCharacters)
            + "😀"
            + new string('p', TailBoundaryOffset - 2)
            + "😀"
            + new string('p', conversationTailCharacters - 1);
        _ = await runtime.RunTurnAsync(oversizedPrompt);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-unicode-context", "capture Unicode safely"));

        Assert.Equal("Completed", response.ExecutionStatus);
        var roleSource = Assert.Single(response.Run!.Context.SourceManifest, source => source.SourceId == "role");
        var conversationSource = Assert.Single(response.Run.Context.SourceManifest, source => source.SourceType == "InvokingConversation" && source.OmissionReason is null);
        Assert.True(roleSource.Truncated);
        Assert.True(conversationSource.Truncated);
        Assert.True(HasValidSurrogatePairs(roleSource.Content));
        Assert.True(HasValidSurrogatePairs(conversationSource.Content));
    }

    [Fact]
    public async Task Public_runtime_rejects_malformed_invocations_and_durably_replays_a_missing_loop_outcome()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var validHash = new string('a', CustomLoopLimits.Sha256HexCharacters);
        var invalidInputs = new[]
        {
            new LoopRunInvocationInput("loop-valid", 1, validHash, "!", "prompt"),
            new LoopRunInvocationInput("!", 1, validHash, "invoke-invalid-loop", "prompt"),
            new LoopRunInvocationInput("loop-valid", 0, validHash, "invoke-invalid-version", "prompt"),
            new LoopRunInvocationInput("loop-valid", 1, validHash.ToUpperInvariant(), "invoke-invalid-hash", "prompt"),
            new LoopRunInvocationInput("loop-valid", 1, validHash, "invoke-invalid-prompt", new string('p', CustomLoopLimits.MaxPresetPromptCharacters + 1))
        };

        foreach (var input in invalidInputs)
        {
            var invalid = await runtime.InvokeCustomLoopAsync(input);
            Assert.Equal("Invalid", invalid.AdmissionStatus);
            Assert.Null(invalid.ExecutionStatus);
            Assert.False(invalid.WasDispatched);
            Assert.Null(invalid.Run);
            Assert.Empty(invalid.ValidationErrors);
            Assert.False(string.IsNullOrWhiteSpace(invalid.Detail));
        }

        var missingInput = new LoopRunInvocationInput("loop-missing", 1, validHash, "invoke-missing-loop", "missing loop prompt");
        var missing = await runtime.InvokeCustomLoopAsync(missingInput);
        var replay = await runtime.InvokeCustomLoopAsync(missingInput);

        Assert.Equal("NotFound", missing.AdmissionStatus);
        Assert.Null(missing.ExecutionStatus);
        Assert.False(missing.WasDispatched);
        Assert.Null(missing.Run);
        Assert.Empty(missing.ValidationErrors);
        Assert.Contains("does not exist", missing.Detail, StringComparison.Ordinal);
        Assert.Equal(missing.AdmissionStatus, replay.AdmissionStatus);
        Assert.Equal(missing.ExecutionStatus, replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Null(replay.Run);
        Assert.Empty(replay.ValidationErrors);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_runtime_preserves_unsupported_discovery_index_cleanup_guidance_during_admission()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-unsupported-index", "update-unsupported-index");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-unsupported-index", "surface cleanup guidance");

        var exception = await Assert.ThrowsAsync<LoopRunEvidenceUnsupportedSchemaException>(() => runtime.InvokeCustomLoopAsync(input));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));

        File.Delete(indexPath);
        var retry = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("Admitted", retry.AdmissionStatus);
    }

    [Fact]
    public async Task Public_runtime_translates_unsupported_discovery_index_schema_for_run_list_reads()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);

        var recentException = await Assert.ThrowsAsync<LoopRunEvidenceUnsupportedSchemaException>(() => runtime.ListCustomLoopRunsAsync());
        var pageException = await Assert.ThrowsAsync<LoopRunEvidenceUnsupportedSchemaException>(() => runtime.ListCustomLoopRunPageAsync());

        Assert.Contains("Delete `.custom-loop-run-index.json`", recentException.Message, StringComparison.Ordinal);
        Assert.Contains("Delete `.custom-loop-run-index.json`", pageException.Message, StringComparison.Ordinal);
        Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));
    }

    [Fact]
    public async Task Invocation_quota_pressure_prunes_expired_completed_receipts_before_accepting_a_new_operation()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteExpiredInvocationReceiptQuotaAsync(paths);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var input = new LoopRunInvocationInput("loop-missing", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-after-receipt-retention", "quota recovery");

        var response = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("NotFound", response.AdmissionStatus);
        Assert.NotNull(await new CustomLoopInvocationOperationStore(paths).GetAsync(input.OperationId));
        Assert.Null(await new CustomLoopInvocationOperationStore(paths).GetAsync("invoke-expired-00000"));
        var audit = await new AuditLog(paths).ReadTailAsync(20);
        Assert.Contains(audit, item => item.Action == AuditSchema.Actions.LoopInvocationReceiptRetentionIntent && item.Outcome == AuditSchema.Outcomes.Requested);
        Assert.Contains(audit, item => item.Action == AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome && item.Outcome == AuditSchema.Outcomes.Succeeded);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopInvocationReceiptRetentionPath, "active.json")));
    }

    [Fact]
    public async Task Completed_invocation_receipt_cannot_replay_after_the_logical_conversation_is_replaced()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var input = new LoopRunInvocationInput("loop-missing", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-cross-conversation", "private prompt");

        var missing = await runtime.InvokeCustomLoopAsync(input);
        var fresh = await runtime.RunTurnAsync("/new");
        var conflict = await runtime.InvokeCustomLoopAsync(input);
        var receipt = await new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath)).GetAsync(input.OperationId);

        Assert.Equal("NotFound", missing.AdmissionStatus);
        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, fresh.Status);
        Assert.Equal("Conflict", conflict.AdmissionStatus);
        Assert.Contains("different logical conversation", conflict.Detail, StringComparison.Ordinal);
        Assert.Equal(CustomLoopInvocationBindingState.ConversationNotFound, receipt!.BindingState);
        Assert.DoesNotContain("private prompt", await File.ReadAllTextAsync(Path.Combine(new WorkspacePaths(workspace.RootPath).CustomLoopInvocationOperationsPath, input.OperationId + ".json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bound_invocation_replay_returns_a_structured_failure_when_conversation_identity_cannot_be_read()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var input = new LoopRunInvocationInput("loop-missing", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-unreadable-conversation", "private prompt");
        Assert.Equal("NotFound", (await runtime.InvokeCustomLoopAsync(input)).AdmissionStatus);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath + ".identity.json", "{ malformed");

        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("Invalid", replay.AdmissionStatus);
        Assert.Contains("conversation identity could not be read safely", replay.Detail, StringComparison.Ordinal);
        Assert.False(replay.WasDispatched);
    }

    [Fact]
    public async Task New_terminal_binding_returns_a_structured_failure_when_conversation_identity_cannot_be_read()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath + ".identity.json", "{ malformed");
        var input = new LoopRunInvocationInput("loop-missing", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-unreadable-new-conversation", "private prompt");

        var response = await runtime.InvokeCustomLoopAsync(input);
        var receipt = Assert.IsType<CustomLoopInvocationOperation>(await new CustomLoopInvocationOperationStore(paths).GetAsync(input.OperationId));

        Assert.Equal("Invalid", response.AdmissionStatus);
        Assert.Contains("conversation identity could not be read safely", response.Detail, StringComparison.Ordinal);
        Assert.False(response.WasDispatched);
        Assert.Equal(CustomLoopInvocationOperationState.Pending, receipt.State);
        Assert.Equal(CustomLoopInvocationBindingState.Unbound, receipt.BindingState);
    }

    [Fact]
    public async Task Rejected_invocation_replay_preserves_structured_validation_errors()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-validation-replay", "update-validation-replay");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, new string('0', CustomLoopLimits.Sha256HexCharacters), "invoke-validation-replay", "validate replay");

        var rejected = await runtime.InvokeCustomLoopAsync(input);
        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("Invalid", rejected.AdmissionStatus);
        var error = Assert.Single(rejected.ValidationErrors, item => item.Code == "definition_conflict");
        Assert.Equal(error, Assert.Single(replay.ValidationErrors, item => item.Code == "definition_conflict"));
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_unavailable_receipt_replays_a_valid_nonterminal_run_relationship()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-audit-relation-replay", "update-audit-relation-replay");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(definitionSnapshot.Id));
        var runStore = new CustomLoopRunStore(paths);
        var referencedRun = await CreateReferencedRunAsync(runStore, definition, "run-audit-relation", "invoke-existing-audit-relation");
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-audit-relation-replay", "audit relation replay");
        await PersistRejectedReceiptAsync(paths, input, definition.RoleId, referencedRun.Id, CustomLoopAdmissionStatus.AuditUnavailable);

        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("AuditUnavailable", replay.AdmissionStatus);
        Assert.Equal(referencedRun.Id, replay.Run!.Id);
        Assert.Equal(referencedRun.Status.ToString(), replay.ExecutionStatus);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_unavailable_receipt_replays_a_valid_operation_conflict_run_relationship()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var requestedDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-audit-conflict-request", "update-audit-conflict-request");
        var conflictingDefinitionSnapshot = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-audit-conflict-run", "update-audit-conflict-run");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var conflictingDefinition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(conflictingDefinitionSnapshot.Id));
        var runStore = new CustomLoopRunStore(paths);
        const string OperationId = "invoke-audit-conflict-replay";
        var referencedRun = await CreateReferencedRunAsync(runStore, conflictingDefinition, "run-audit-conflict", OperationId);
        var input = new LoopRunInvocationInput(requestedDefinition.Id, requestedDefinition.DefinitionVersion, requestedDefinition.ContentHash, OperationId, "audit conflict replay");
        await PersistRejectedReceiptAsync(paths, input, requestedDefinition.RoleId, referencedRun.Id, CustomLoopAdmissionStatus.AuditUnavailable);

        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("AuditUnavailable", replay.AdmissionStatus);
        Assert.Equal(referencedRun.Id, replay.Run!.Id);
        Assert.Equal(referencedRun.Status.ToString(), replay.ExecutionStatus);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_receipt_replays_against_its_intentionally_deleted_run_tombstone()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-deleted-rejection-replay", "update-deleted-rejection-replay");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(definitionSnapshot.Id));
        var runStore = new CustomLoopRunStore(paths);
        var referencedRun = await CreateReferencedRunAsync(runStore, definition, "run-deleted-rejection", "invoke-existing-deleted-rejection");
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-deleted-rejection-replay", "deleted rejection replay");
        await PersistRejectedReceiptAsync(paths, input, definition.RoleId, referencedRun.Id, CustomLoopAdmissionStatus.NonterminalRunExists);
        var running = AdvanceRun(referencedRun, CustomLoopRunStatus.Running);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runStore.UpdateAsync(running, referencedRun.LifecycleVersion)).Status);
        var completed = AdvanceRun(running, CustomLoopRunStatus.Completed);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runStore.UpdateAsync(completed, running.LifecycleVersion)).Status);
        var inspection = Assert.IsType<CustomLoopTraceInspection>(await runStore.InspectTraceAsync(completed.Id));
        var deletionRequest = new CustomLoopTraceDeletionRequest(completed.Id, inspection.PersistedArtifactHash, "delete-rejected-reference", WorkspaceActors.Cli, "cli");
        var deletion = new CustomLoopTraceDeletionMutation(deletionRequest, CustomLoopTraceDeletionRequestHash.Compute(deletionRequest), completed.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Deleted, (await runStore.DeleteTerminalTraceAsync(deletion)).Status);

        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("NonterminalRunExists", replay.AdmissionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), replay.ExecutionStatus);
        Assert.Null(replay.Run);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Definition_read_failure_is_bound_and_replayed_without_repeating_the_failed_read()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-invalid-definition-replay", "update-invalid-definition-replay");
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopDefinitionsPath, definition.Id + ".json"), "{ malformed");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-invalid-definition-replay", "private prompt");

        var rejected = await runtime.InvokeCustomLoopAsync(input);
        var replay = await runtime.InvokeCustomLoopAsync(input);
        var receipt = Assert.IsType<CustomLoopInvocationOperation>(await new CustomLoopInvocationOperationStore(paths).GetAsync(input.OperationId));

        Assert.Equal("Invalid", rejected.AdmissionStatus);
        Assert.Equal("Invalid", replay.AdmissionStatus);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, receipt.State);
        Assert.Equal(CustomLoopInvocationBindingState.ConversationInvalid, receipt.BindingState);
        Assert.DoesNotContain("private prompt", await File.ReadAllTextAsync(Path.Combine(paths.CustomLoopInvocationOperationsPath, input.OperationId + ".json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captured_receipt_retains_its_context_binding_when_a_retried_definition_read_fails()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-captured-definition-failure", "update-captured-definition-failure");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-captured-definition-failure", "private prompt");
        var receiptStore = new CustomLoopInvocationOperationStore(paths);
        var pending = PendingInvocation(input, definition.RoleId);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await receiptStore.BeginAsync(pending)).Status);
        var captured = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version,
            ContextIdentityHash = new string('c', CustomLoopLimits.Sha256HexCharacters),
            Detail = "The invocation context was captured before interruption."
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await receiptStore.BindAsync(captured)).Status);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopDefinitionsPath, definition.Id + ".json"), "{ malformed");

        var response = await runtime.InvokeCustomLoopAsync(input);
        var completed = Assert.IsType<CustomLoopInvocationOperation>(await receiptStore.GetAsync(input.OperationId));

        Assert.Equal("Invalid", response.AdmissionStatus);
        Assert.False(response.WasDispatched);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, completed.State);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, completed.BindingState);
        Assert.Equal(captured.ContextIdentityHash, completed.ContextIdentityHash);
    }

    [Fact]
    public async Task Pending_workspace_busy_binding_completes_its_selected_outcome_after_the_workspace_becomes_free()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-pending-busy-replay", "update-pending-busy-replay");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-pending-busy-replay", "must not dispatch");
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);
        var pending = PendingInvocation(input, definition.RoleId);
        var store = new CustomLoopInvocationOperationStore(paths);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        pending = pending with
        {
            BindingState = CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy,
            InvokingConversationId = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version,
            Detail = "workspace_execution_busy: the no-dispatch outcome was selected before interruption."
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(pending)).Status);

        var completed = await runtime.InvokeCustomLoopAsync(input);
        var replay = await runtime.InvokeCustomLoopAsync(input);
        var receipt = Assert.IsType<CustomLoopInvocationOperation>(await store.GetAsync(input.OperationId));

        Assert.Equal("WorkspaceExecutionBusy", completed.AdmissionStatus);
        Assert.Equal("WorkspaceExecutionBusy", replay.AdmissionStatus);
        Assert.False(completed.WasDispatched);
        Assert.False(replay.WasDispatched);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, receipt.State);
        Assert.Equal(CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy, receipt.BindingState);
        Assert.DoesNotContain(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == definition.Id);
    }

    [Fact]
    public async Task Context_capture_bounds_selected_conversation_entries_and_aggregates_all_omissions_once()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-entry-cap", "update-runtime-entry-cap");
        await using var runtime = await CreateRuntimeAsync(workspace);
        for (var index = 0; index < (CustomLoopLimits.MaxInvokingConversationEntries / 2) + 1; index++)
        {
            _ = await runtime.RunTurnAsync("x");
        }

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-entry-cap", "entry cap test"));
        var conversation = response.Run!.Context.SourceManifest.Where(source => source.SourceType == "InvokingConversation").ToArray();

        Assert.InRange(conversation.Count(source => source.OmissionReason is null), 1, CustomLoopLimits.MaxInvokingConversationEntries);
        var omission = Assert.Single(conversation, source => source.OmissionReason is not null);
        Assert.Equal("invoking-conversation-omitted", omission.SourceId);
        Assert.Contains("message(s) were omitted", omission.OmissionReason, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, response.Run.Context.SourceManifest.Count), response.Run.Context.SourceManifest.Select(source => source.Order));
    }

    [Fact]
    public async Task Public_runtime_admits_executes_publishes_and_exposes_inspectable_artifacts_without_changing_default_turns()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(".agent", "ROLE.md"), "role context evidence");
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-success", "update-runtime-success");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var prior = await runtime.RunTurnAsync("prior logical prompt");
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-success", "custom task");

        var response = await runtime.InvokeCustomLoopAsync(input);
        var fetched = await runtime.GetCustomLoopRunAsync(response.Run!.Id);
        var listed = await runtime.ListCustomLoopRunsAsync();
        var replay = await runtime.InvokeCustomLoopAsync(input);
        var persistedConversationAfterReplay = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationSnapshotAsync();
        var ordinaryTurnAfterCustomRun = await runtime.RunTurnAsync("ordinary turn still works");

        Assert.Equal("MessageCompleted", prior.Status.ToString());
        Assert.Equal("Admitted", response.AdmissionStatus);
        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.True(response.WasDispatched);
        Assert.Equal("Completed", response.Run.Status);
        Assert.Equal("OpenAiCodex", response.Run.Model.Provider);
        Assert.Equal("test-model", response.Run.Model.Model);
        Assert.Equal(definition.ContentHash, response.Run.AdmittedDefinition.ContentHash);
        Assert.Equal(persistedConversationAfterReplay.Version, response.Run.InvokingConversation!.ConversationId);
        Assert.Empty(response.Run.Context.InvokingConversationMessages);
        var roleSource = Assert.Single(response.Run.Context.SourceManifest, source => source.SourceId == "role");
        Assert.Equal("RoleInstruction", roleSource.SourceType);
        Assert.Equal("TrustedInstruction", roleSource.TrustClass);
        Assert.Contains("role context evidence", roleSource.Content, StringComparison.Ordinal);
        var identitySource = Assert.Single(response.Run.Context.SourceManifest, source => source.SourceId == "soul");
        Assert.Equal("AgentIdentity", identitySource.SourceType);
        Assert.Equal("WorkspaceAgentIdentityFile", identitySource.Provenance);
        Assert.Equal("TrustedInstruction", identitySource.TrustClass);
        var startedAttempt = Assert.Single(response.Run.Events, runEvent => runEvent.Kind == "NodeAttemptStarted");
        Assert.NotEmpty(startedAttempt.ContextBlocks);
        Assert.NotNull(startedAttempt.ToolAuthority);
        Assert.Empty(startedAttempt.ToolAuthority.EffectiveAssignments);
        Assert.Null(startedAttempt.ToolEvidence);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == true);
        Assert.NotNull(response.Run.FinalOutput);
        AssertInspectableProjection(response.Run, Assert.Single(listed, summary => summary.Id == response.Run.Id));
        Assert.Equal(response.Run.Id, fetched!.Id);
        Assert.Equal(response.Run.Status, fetched.Status);
        Assert.Equal(response.Run.Events.Count, fetched.Events.Count);
        Assert.Equal(response.Run.Context.ManifestHash, fetched.Context.ManifestHash);
        Assert.Contains(listed, summary => summary.Id == response.Run.Id && summary.Status == "Completed");

        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.Equal("Completed", replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Equal(response.Run.Id, replay.Run!.Id);
        Assert.Equal(response.Run.Events.Count, replay.Run.Events.Count);
        Assert.Equal(3, persistedConversationAfterReplay.Messages.Count);
        Assert.Equal(response.Run.FinalOutput, persistedConversationAfterReplay.Messages[^1].Content);
        Assert.Equal("MessageCompleted", ordinaryTurnAfterCustomRun.Status.ToString());
        Assert.Equal("default-conversation", ordinaryTurnAfterCustomRun.RunIdentity!.LoopId);
    }

    [Fact]
    public async Task Public_runtime_refreshes_durable_conversation_before_custom_loop_context_capture()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-peer-conversation", "update-runtime-peer-conversation");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var conversationMemory = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        await conversationMemory.AppendMessageAsync(LlmMessage.User("peer durable prompt"));

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-peer-conversation", "custom task"));
        var persistedConversation = await conversationMemory.LoadCurrentConversationAsync();

        Assert.Equal("Completed", response.ExecutionStatus);
        var admittedMessage = Assert.Single(response.Run!.Context.InvokingConversationMessages);
        Assert.Contains("peer durable prompt", admittedMessage.Content, StringComparison.Ordinal);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == true);
        Assert.Equal("peer durable prompt", persistedConversation[0].Content);
        Assert.Equal(response.Run.FinalOutput, persistedConversation[^1].Content);
    }

    [Fact]
    public async Task Context_capture_rejects_local_and_durable_conversation_divergence_without_overwriting_local_state()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-divergent-context", "update-runtime-divergent-context");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var conversationMemory = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        var defaultTurn = runtime.RunTurnAsync("delayed default turn");
        await WaitForAttemptStartAsync(workspace);
        await conversationMemory.StartFreshConversationAsync();
        var divergentDefaultTurn = await defaultTurn;
        Assert.Equal("MessageNeedsReview", divergentDefaultTurn.Status.ToString());
        Assert.Contains("Existing user-owned content was preserved", divergentDefaultTurn.FailureDetail, StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-divergent-context", "custom task")));
        var followingTurn = await runtime.RunTurnAsync("following default turn");

        Assert.Contains("diverged", exception.Message, StringComparison.Ordinal);
        Assert.Equal("MessageFailed", followingTurn.Status.ToString());
        Assert.Contains("Active local context was preserved", followingTurn.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversation_publication_rejects_a_replaced_durable_conversation_with_the_same_transcript()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-conversation-identity", "update-runtime-conversation-identity");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var conversationMemory = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        var originalIdentity = (await conversationMemory.LoadCurrentConversationSnapshotAsync()).Version;
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-conversation-identity", "delayed custom task"));
        await WaitForAttemptStartAsync(workspace);
        await conversationMemory.StartFreshConversationAsync();
        var replacementIdentity = (await conversationMemory.LoadCurrentConversationSnapshotAsync()).Version;

        var response = await invocation;
        var persistedConversation = await conversationMemory.LoadCurrentConversationAsync();

        Assert.NotEqual(originalIdentity, replacementIdentity);
        Assert.Equal("Failed", response.ExecutionStatus);
        Assert.Equal("conversation_publication_failed", response.Run!.FailureCode);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == false);
        Assert.Empty(persistedConversation);
    }

    [Fact]
    public async Task Runtime_publishes_multiple_node_and_Exit_outputs_against_the_admission_prefix_plus_the_exact_durable_run_suffix()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-runtime-sequential-publication")).Definition);
        var publishInference = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Inference.ContextIn, new LoopContextOutputPolicy(true, true)));
        var publishExit = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Exit.ContextIn, new LoopContextOutputPolicy(false, true)));
        var input = new LoopDefinitionInput(
            "Sequential publication loop",
            "Publishes two inference outputs and the terminal result.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [
                new LoopInferenceStep(created.InferenceSteps.Single().Id, "First", "Produce the first result.", publishInference),
                new LoopInferenceStep(null, "Second", "Produce the second result.", publishInference)
            ],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, publishExit));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-runtime-sequential-publication", input);
        var definition = Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-sequential-publication", "publish sequentially"));
        var persistedConversation = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();
        var publications = response.Run!.Events.Where(item => item.Kind == "ConversationPublished" && item.PublishedToInvokingConversation == true).ToArray();

        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Equal(3, publications.Length);
        Assert.Equal(3, persistedConversation.Count);
        Assert.Equal(publications.Select(item => item.CanonicalOutput), persistedConversation.Select(item => item.Content));
    }

    [Fact]
    public async Task Runtime_notifies_each_verified_conversation_publication_in_durable_order()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-runtime-publication-observer")).Definition);
        var publishInference = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Inference.ContextIn, new LoopContextOutputPolicy(true, true)));
        var noPublishExit = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Exit.ContextIn, new LoopContextOutputPolicy(false, false)));
        var input = new LoopDefinitionInput(
            "Observed publication loop",
            "Publishes one verified inference output.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Publish", "Publish the result.", publishInference)],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, noPublishExit));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-runtime-publication-observer", input);
        var definition = Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
        var observer = new RecordingConversationPublicationObserver();
        await using var runtime = await CreateRuntimeAsync(workspace, observer);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-publication-observer", "publish once"));

        Assert.Equal("Completed", response.ExecutionStatus);
        var publication = Assert.Single(observer.Publications);
        Assert.Equal(response.Run!.Id, publication.RunId);
        Assert.Equal(definition.Id, publication.LoopId);
        Assert.Equal(1, publication.MessageCount);
        Assert.False(publication.AlreadyPublished);
        Assert.Equal(Assert.Single(response.Run.Events, item => item.Kind == "ConversationPublished").ConversationPublicationId, publication.OperationId);
    }

    [Fact]
    public async Task Admission_captures_bounded_labeled_role_sources_and_a_versioned_newest_conversation_snapshot()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var roleSource = new string('R', 12_050);
        await File.WriteAllTextAsync(workspace.File(".agent", "ROLE.md"), roleSource);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-context", "update-runtime-context");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var oversizedPrompt = "prompt-head-" + new string('x', CustomLoopLimits.MaxInvokingConversationCharacters + 500) + "-prompt-tail";
        _ = await runtime.RunTurnAsync(oversizedPrompt);

        var first = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-context-1", "first custom task"));
        var second = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-context-2", "second custom task"));

        var manifest = first.Run!.Context.SourceManifest;
        Assert.Equal(["nearest-agents", "role", "soul", "personality", "context", "memory", "models"], manifest.Take(7).Select(source => source.SourceId));
        Assert.Equal(Enumerable.Range(1, manifest.Count), manifest.Select(source => source.Order));
        var missingAgents = manifest[0];
        Assert.False(string.IsNullOrWhiteSpace(missingAgents.OmissionReason));
        Assert.Equal(string.Empty, missingAgents.Content);
        Assert.Equal(0, missingAgents.UsedCharacterCount);
        Assert.False(missingAgents.Truncated);
        var roleManifestSource = Assert.Single(manifest, source => source.SourceId == "role");
        Assert.Equal("RoleInstruction", roleManifestSource.SourceType);
        Assert.Equal("WorkspaceRoleFile", roleManifestSource.Provenance);
        Assert.Equal("TrustedInstruction", roleManifestSource.TrustClass);
        Assert.Equal("system", roleManifestSource.Role);
        Assert.EndsWith(".agent/ROLE.md", roleManifestSource.SourcePath.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Equal(CustomLoopLimits.MaxInstructionCharacters, roleManifestSource.UsedCharacterCount);
        Assert.Equal(roleManifestSource.UsedCharacterCount, roleManifestSource.Content.Length);
        Assert.True(roleManifestSource.OriginalCharacterCount > roleManifestSource.UsedCharacterCount);
        Assert.True(roleManifestSource.Truncated);
        Assert.NotNull(roleManifestSource.TruncationReason);
        Assert.Null(roleManifestSource.OmissionReason);
        Assert.Contains("[source truncated to fit the 12000-character admitted source limit]", roleManifestSource.Content, StringComparison.Ordinal);
        var soulManifestSource = Assert.Single(manifest, source => source.SourceId == "soul");
        Assert.Equal("AgentIdentity", soulManifestSource.SourceType);
        Assert.Equal("WorkspaceAgentIdentityFile", soulManifestSource.Provenance);
        Assert.Equal("TrustedInstruction", soulManifestSource.TrustClass);
        Assert.Equal("system", soulManifestSource.Role);
        var contextualState = Assert.Single(manifest, source => source.SourceId == "memory");
        Assert.Equal("ContextualState", contextualState.SourceType);
        Assert.Equal("WorkspaceContextFile", contextualState.Provenance);
        Assert.Equal("UntrustedData", contextualState.TrustClass);
        Assert.Equal("user", contextualState.Role);
        var conversationMessage = Assert.Single(manifest, source => source.SourceType == "InvokingConversation" && source.OmissionReason is null);
        Assert.Equal("LogicalConversation", conversationMessage.Provenance);
        Assert.Equal("UntrustedData", conversationMessage.TrustClass);
        Assert.Equal("user", conversationMessage.Role);
        Assert.Equal(CustomLoopLimits.MaxInvokingConversationCharacters, conversationMessage.UsedCharacterCount);
        Assert.True(conversationMessage.OriginalCharacterCount > conversationMessage.UsedCharacterCount);
        Assert.True(conversationMessage.Truncated);
        Assert.NotNull(conversationMessage.TruncationReason);
        Assert.Contains("[truncated to 24000 characters for invoking-conversation admission]", conversationMessage.Content, StringComparison.Ordinal);
        Assert.Contains("fake response: prompt-head-", conversationMessage.Content, StringComparison.Ordinal);
        Assert.EndsWith("-prompt-tail", conversationMessage.Content, StringComparison.Ordinal);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, first.Run.InvokingConversation!.CapturedVersion.Length);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, first.Run.Context.ManifestHash.Length);
        Assert.NotEqual(first.Run.InvokingConversation.CapturedVersion, second.Run!.InvokingConversation!.CapturedVersion);
        Assert.True(second.Run.Context.CapturedAtUtc >= first.Run.Context.CapturedAtUtc);
    }

    [Fact]
    public async Task Replay_of_a_valid_historical_run_without_an_invoking_conversation_reaches_admission_without_throwing_or_dispatching()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-null-conversation", "update-runtime-null-conversation");
        var paths = new WorkspacePaths(workspace.RootPath);
        var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(definitionSnapshot.Id));
        var now = DateTimeOffset.UtcNow;
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var admittedEvent = new CustomLoopRunEvent(
            1,
            "event-legacy-no-conversation",
            now,
            CustomLoopRunEventKind.Admitted,
            null,
            null,
            null,
            "Historical admission without a conversation destination.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            "OpenAiCodex",
            "test-model",
            null,
            null);
        var admissionAuditCompleted = new CustomLoopRunEvent(
            2,
            "event-legacy-no-conversation-audit",
            now,
            CustomLoopRunEventKind.AdmissionAuditCompleted,
            null,
            null,
            null,
            "Historical admission audit completed.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-legacy-no-conversation",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            "cli",
            new CustomLoopModelSnapshot("OpenAiCodex", "test-model"),
            "invoke-legacy-no-conversation",
            WorkspaceActors.Cli,
            new string('0', CustomLoopLimits.Sha256HexCharacters),
            definition,
            "legacy prompt",
            null,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admittedEvent, admissionAuditCompleted],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        var runStore = new CustomLoopRunStore(paths);
        var pendingAdmission = run with { LifecycleVersion = 1, Events = [admittedEvent] };
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await runStore.CreateAsync(pendingAdmission)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runStore.UpdateAsync(run, expectedLifecycleVersion: 1)).Status);
        await using var runtime = await CreateRuntimeWithoutProviderAsync(workspace);

        var replay = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, run.AdmissionOperationId, run.TriggerPrompt));
        var receipt = Assert.IsType<CustomLoopInvocationOperation>(await new CustomLoopInvocationOperationStore(paths).GetAsync(run.AdmissionOperationId));

        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.Equal("Paused", replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Null(replay.Run!.InvokingConversation);
        Assert.Equal(run.Id, replay.Run.Id);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, receipt.BindingState);
    }

    [Fact]
    public async Task Conversation_publication_recognizes_the_exact_expected_prefix_plus_one_output_as_already_published()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-idempotent-publish", "update-runtime-idempotent-publish");
        const string Prompt = "delayed idempotent task";
        var expectedOutput = "fake response: [EmbodySense untrusted trigger prompt data]" + Environment.NewLine + Prompt;
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-idempotent-publish", Prompt));
        await WaitForAttemptStartAsync(workspace);
        await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).AppendMessageAsync(LlmMessage.Assistant(expectedOutput));

        var history = await runtime.RunTurnAsync("/history");
        var loaded = await runtime.RunTurnAsync("1");
        var response = await invocation;
        var persistedConversation = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();

        Assert.Equal("CommandHandled", history.Status.ToString());
        Assert.Equal("CommandHandled", loaded.Status.ToString());
        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Equal(expectedOutput, response.Run!.FinalOutput);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.Detail.Contains("already committed", StringComparison.Ordinal));
        Assert.Collection(persistedConversation, message => Assert.Equal(expectedOutput, message.Content));
    }

    [Fact]
    public async Task Conversation_publication_definitely_fails_when_the_logical_conversation_changes_after_admission()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-publication-conflict", "update-runtime-publication-conflict");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-publication-conflict", "delayed custom task"));
        await WaitForAttemptStartAsync(workspace);

        var interleavingTurn = await runtime.RunTurnAsync("interleaving ordinary turn");
        var response = await invocation;

        Assert.Equal("MessageCompleted", interleavingTurn.Status.ToString());
        Assert.Equal("Failed", response.ExecutionStatus);
        Assert.Equal("Failed", response.Run!.Status);
        Assert.Equal("conversation_publication_failed", response.Run.FailureCode);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == false);
    }

    [Fact]
    public async Task Conversation_append_exception_is_reconciled_as_definitely_failed_when_no_append_occurred()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-append-failure", "update-runtime-append-failure");
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-append-failure", "delayed append failure"));
        await WaitForAttemptStartAsync(workspace);
        File.SetAttributes(paths.CurrentConversationPath, FileAttributes.ReadOnly);

        LoopRunInvocationResponse response;
        try
        {
            response = await invocation;
        }
        finally
        {
            File.SetAttributes(paths.CurrentConversationPath, FileAttributes.Normal);
        }

        Assert.Equal("Failed", response.ExecutionStatus);
        Assert.Equal("conversation_publication_failed", response.Run!.FailureCode);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == false);
        Assert.Empty(await new ConversationMemoryStore(paths).LoadCurrentConversationAsync());
    }

    [Fact]
    public async Task Concurrent_different_loop_is_durably_rejected_as_workspace_busy_without_context_capture_or_hidden_queueing()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var firstDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-no-queue-1", "update-runtime-no-queue-1");
        var secondDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-no-queue-2", "update-runtime-no-queue-2");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var first = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(firstDefinition.Id, firstDefinition.DefinitionVersion, firstDefinition.ContentHash, "invoke-runtime-no-queue-1", "delayed queue owner"));
        await WaitForAttemptStartAsync(workspace);

        var secondInput = new LoopRunInvocationInput(secondDefinition.Id, secondDefinition.DefinitionVersion, secondDefinition.ContentHash, "invoke-runtime-no-queue-2", "second invocation");
        var second = runtime.InvokeCustomLoopAsync(secondInput);
        var firstCompletion = await Task.WhenAny(first, second);
        var rejected = await second;
        var completed = await first;
        var busyReplay = await runtime.InvokeCustomLoopAsync(secondInput);
        var changedContent = await runtime.InvokeCustomLoopAsync(secondInput with { InvocationPrompt = "changed invocation" });
        var runsBeforeFreshOperation = await runtime.ListCustomLoopRunsAsync();
        var admittedAfterRelease = await runtime.InvokeCustomLoopAsync(secondInput with { OperationId = "invoke-runtime-no-queue-3" });

        Assert.Same(second, firstCompletion);
        Assert.Equal("WorkspaceExecutionBusy", rejected.AdmissionStatus);
        Assert.False(rejected.WasDispatched);
        Assert.Null(rejected.Run);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal("WorkspaceExecutionBusy", busyReplay.AdmissionStatus);
        Assert.False(busyReplay.WasDispatched);
        Assert.Equal("Conflict", changedContent.AdmissionStatus);
        Assert.DoesNotContain(runsBeforeFreshOperation, run => run.LoopId == secondDefinition.Id);
        Assert.Equal("Completed", admittedAfterRelease.ExecutionStatus);
        var receiptPath = Path.Combine(new WorkspacePaths(workspace.RootPath).CustomLoopInvocationOperationsPath, secondInput.OperationId + ".json");
        Assert.True(File.Exists(receiptPath));
        Assert.Contains("workspaceExecutionBusy", await File.ReadAllTextAsync(receiptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_same_operation_has_one_owner_and_replays_its_admitted_run_without_redispatch()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-same-operation", "update-runtime-same-operation");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-same-operation", "delayed same operation owner");

        var first = runtime.InvokeCustomLoopAsync(input);
        await WaitForAttemptStartAsync(workspace);
        var concurrent = await runtime.InvokeCustomLoopAsync(input);
        var completed = await first;
        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("Admitted", concurrent.AdmissionStatus);
        Assert.False(concurrent.WasDispatched);
        Assert.NotNull(concurrent.Run);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(completed.Run!.Id, concurrent.Run!.Id);
        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Equal(completed.Run.Id, replay.Run!.Id);
        Assert.Equal(completed.Run.Events.Count, replay.Run.Events.Count);
    }

    [Fact]
    public async Task Paused_run_releases_workspace_ownership_and_resume_busy_is_replayed_without_mutation_or_dispatch()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var pausedDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-paused-owner", "update-runtime-paused-owner");
        var competingDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-resume-busy", "update-runtime-resume-busy");
        await using var runtime = await CreateRuntimeAsync(workspace);

        var pausingInvocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(pausedDefinition.Id, pausedDefinition.DefinitionVersion, pausedDefinition.ContentHash, "invoke-runtime-paused-owner", "held pause owner"));
        await WaitForAttemptStartAsync(workspace);
        var running = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == pausedDefinition.Id);
        var pause = await runtime.PauseCustomLoopAsync(new LoopRunControlInput(running.Id, (await runtime.GetCustomLoopRunAsync(running.Id))!.LifecycleVersion, "pause-runtime-owner"));
        File.WriteAllText(workspace.File("custom-attempt-release.marker"), "released");
        var paused = await pausingInvocation;

        Assert.Equal("PauseRequested", pause.Status);
        Assert.Equal("Paused", paused.ExecutionStatus);
        Assert.Equal("Paused", paused.Run!.Status);
        var duplicateInput = new LoopRunInvocationInput(pausedDefinition.Id, pausedDefinition.DefinitionVersion, pausedDefinition.ContentHash, "invoke-runtime-nonterminal-rejection", "must not dispatch");
        var nonterminal = await runtime.InvokeCustomLoopAsync(duplicateInput);
        var nonterminalReplay = await runtime.InvokeCustomLoopAsync(duplicateInput);
        Assert.Equal("NonterminalRunExists", nonterminal.AdmissionStatus);
        Assert.Equal(paused.Run.Id, nonterminal.Run!.Id);
        Assert.Equal("NonterminalRunExists", nonterminalReplay.AdmissionStatus);
        Assert.Equal(paused.Run.Id, nonterminalReplay.Run!.Id);
        Assert.Contains("replayed", nonterminalReplay.Detail, StringComparison.OrdinalIgnoreCase);

        File.Delete(workspace.File("custom-attempt-started.marker"));
        var competitor = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(competingDefinition.Id, competingDefinition.DefinitionVersion, competingDefinition.ContentHash, "invoke-runtime-resume-competitor", "held resume competitor"));
        await WaitForAttemptStartAsync(workspace);
        var resumeInput = new LoopRunControlInput(paused.Run.Id, paused.Run.LifecycleVersion, "resume-runtime-busy");
        var busy = await runtime.ResumeCustomLoopAsync(resumeInput);
        var busyReplay = await runtime.ResumeCustomLoopAsync(resumeInput);
        var pausedAfterBusy = await runtime.GetCustomLoopRunAsync(paused.Run.Id);

        Assert.Equal("WorkspaceExecutionBusy", busy.Status);
        Assert.Equal("WorkspaceExecutionBusy", busyReplay.Status);
        Assert.Equal(paused.Run.LifecycleVersion, pausedAfterBusy!.LifecycleVersion);
        Assert.Equal("Paused", pausedAfterBusy.Status);
        Assert.Equal(paused.Run.ExecutionClock, pausedAfterBusy.ExecutionClock);
        Assert.Equal(paused.Run.Checkpoint.LastCommittedSequence, pausedAfterBusy.Checkpoint.LastCommittedSequence);
        Assert.Equal(paused.Run.Events.Count, pausedAfterBusy.Events.Count);

        var competingRun = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == competingDefinition.Id);
        var competingDetail = (await runtime.GetCustomLoopRunAsync(competingRun.Id))!;
        _ = await runtime.CancelCustomLoopAsync(new LoopRunControlInput(competingRun.Id, competingDetail.LifecycleVersion, "cancel-runtime-resume-competitor"));
        File.WriteAllText(workspace.File("custom-attempt-release.marker"), "released");
        var competitorOutcome = await competitor;
        Assert.Contains(competitorOutcome.Run!.Status, new[] { "Cancelled", "NeedsReview" });

        var successfulResumeInput = resumeInput with { OperationId = "resume-runtime-after-release" };
        var resumed = await runtime.ResumeCustomLoopAsync(successfulResumeInput);
        var completedReplay = await runtime.ResumeCustomLoopAsync(successfulResumeInput);
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal("Completed", resumed.Run!.Status);
        Assert.Equal("Completed", completedReplay.Status);
        Assert.Equal("Completed", completedReplay.Run!.Status);
        Assert.Equal(resumed.Run.Events.Count, completedReplay.Run.Events.Count);
    }

    [Fact]
    public async Task Restart_preserves_the_current_conversation_bound_to_a_paused_run_before_explicit_resume()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-runtime-restart-conversation")).Definition);
        var input = new LoopDefinitionInput(
            "Restart conversation loop",
            "Publishes a later inference result after an explicit post-restart resume.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [
                new LoopInferenceStep(created.InferenceSteps.Single().Id, "First", "Produce the first result.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)),
                new LoopInferenceStep(null, "Second", "Produce the second result.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))
            ],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var definition = Assert.IsType<LoopDefinitionSnapshot>((await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-runtime-restart-conversation", input)).Definition);
        LoopRunSnapshot paused;
        await using (var runtime = await CreateRuntimeAsync(workspace))
        {
            _ = await runtime.RunTurnAsync("conversation prefix before the paused run");
            var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-restart-conversation", "held-once restart conversation"));
            await WaitForAttemptStartAsync(workspace);
            var running = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == definition.Id);
            var pause = await runtime.PauseCustomLoopAsync(new LoopRunControlInput(running.Id, (await runtime.GetCustomLoopRunAsync(running.Id))!.LifecycleVersion, "pause-runtime-restart-conversation"));
            File.WriteAllText(workspace.File("custom-attempt-release.marker"), "released");
            paused = Assert.IsType<LoopRunSnapshot>((await invocation).Run);
            Assert.Equal("PauseRequested", pause.Status);
            Assert.Equal("Paused", paused.Status);
            Assert.Equal(1, paused.Checkpoint.NextStepIndex);
        }

        var conversationStore = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        var beforeRestart = await conversationStore.LoadCurrentConversationAsync();
        await using var restarted = await CreateRuntimeAsync(workspace);
        var afterRestart = await conversationStore.LoadCurrentConversationAsync();
        var resumed = await restarted.ResumeCustomLoopAsync(new LoopRunControlInput(paused.Id, paused.LifecycleVersion, "resume-runtime-restart-conversation"));
        var afterResume = await conversationStore.LoadCurrentConversationAsync();

        Assert.Equal(beforeRestart, afterRestart);
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal("Completed", resumed.Run!.Status);
        Assert.Contains(resumed.Run.Events, runEvent => runEvent.Sequence > paused.Events[^1].Sequence && runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == true);
        Assert.True(afterResume.Count > afterRestart.Count);
    }

    private static void AssertInspectableProjection(LoopRunSnapshot run, LoopRunSummarySnapshot summary)
    {
        Assert.Equal(CustomLoopRunRecord.CurrentSchemaVersion, run.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(run.Id));
        Assert.False(string.IsNullOrWhiteSpace(run.LoopId));
        Assert.True(run.LifecycleVersion > 1);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.CreatedAtUtc <= run.UpdatedAtUtc);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Equal("cli", run.Surface);
        Assert.False(string.IsNullOrWhiteSpace(run.Model.Provider));
        Assert.Equal("test-model", run.Model.Model);
        Assert.False(string.IsNullOrWhiteSpace(run.AdmissionOperationId));
        Assert.Equal(WorkspaceActors.Cli, run.AdmissionActor);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, run.AdmissionRequestHash.Length);
        Assert.Equal(run.LoopId, run.AdmittedDefinition.Id);
        Assert.False(string.IsNullOrWhiteSpace(run.TriggerPrompt));
        Assert.NotNull(run.InvokingConversation);
        Assert.Equal(run.Context.CapturedAtUtc, run.InvokingConversation!.CapturedAtUtc);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, run.Context.ManifestHash.Length);
        Assert.NotEmpty(run.Context.WorkspaceContextMessages);
        Assert.Empty(run.Context.InvokingConversationMessages);
        Assert.True(run.ExecutionClock.AccumulatedRunningMilliseconds >= 0);
        Assert.Null(run.ExecutionClock.ActiveSinceUtc);
        Assert.Equal(1, run.Checkpoint.Iteration);
        Assert.Equal(1, run.Checkpoint.NextStepIndex);
        Assert.Equal(0, run.Checkpoint.AcceptedRepeatCount);
        Assert.False(run.Checkpoint.PendingExitDecision);
        Assert.NotEmpty(run.Checkpoint.EarlierRetainedOutputs);
        Assert.Null(run.Checkpoint.PreviousIterationResult);
        var retained = Assert.IsType<LoopRunRetainedOutputSnapshot>(run.Checkpoint.CurrentIterationResult);
        Assert.False(string.IsNullOrWhiteSpace(retained.StepId));
        Assert.Equal(1, retained.Iteration);
        Assert.Equal(run.FinalOutput, retained.Content);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, retained.ContentHash.Length);
        Assert.Equal(0, run.Checkpoint.ToolRequestsUsed);
        Assert.True(run.Checkpoint.LastCommittedSequence > 0);
        Assert.Null(run.FailureCode);
        Assert.Null(run.FailureDetail);

        var attempt = Assert.Single(run.Events, runEvent => runEvent.Kind == "NodeAttemptStarted");
        Assert.True(attempt.Sequence > 0);
        Assert.False(string.IsNullOrWhiteSpace(attempt.EventId));
        Assert.True(attempt.TimestampUtc >= run.CreatedAtUtc);
        Assert.Equal(1, attempt.Iteration);
        Assert.False(string.IsNullOrWhiteSpace(attempt.StepId));
        Assert.Equal(1, attempt.Attempt);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Detail));
        Assert.Null(attempt.CanonicalOutput);
        Assert.Null(attempt.OriginalOutputCharacterCount);
        Assert.Null(attempt.CanonicalOutputTruncated);
        Assert.Null(attempt.RetainedForLoopReasoning);
        Assert.Null(attempt.PublishedToInvokingConversation);
        Assert.Null(attempt.ConversationPublicationId);
        Assert.Equal("OpenAiCodex", attempt.Provider);
        Assert.Equal("test-model", attempt.Model);
        Assert.False(string.IsNullOrWhiteSpace(attempt.ProviderResponseId));
        Assert.Null(attempt.ExitDecision);
        var block = Assert.Single(attempt.ContextBlocks, contextBlock => contextBlock.Source == "HarnessGovernance");
        Assert.Equal("harness-governance", block.SourceId);
        Assert.Equal("system", block.Role);
        Assert.True(block.Included);
        Assert.Null(block.OmissionReason);
        Assert.False(string.IsNullOrWhiteSpace(block.Content));
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, block.ContentHash.Length);
        Assert.Equal(block.Content.Length, block.CharacterCount);
        Assert.False(block.Truncated);
        Assert.Equal(EmbodySenseDeveloperInstructions.CurrentVersion, block.SourceVersion);
        Assert.Equal(EmbodySenseDeveloperInstructions.Capture().Content, block.Content);

        Assert.Equal(run.Id, summary.Id);
        Assert.Equal(run.LoopId, summary.LoopId);
        Assert.Equal(run.AdmissionOperationId, summary.AdmissionOperationId);
        Assert.Equal(run.AdmittedDefinition.DefinitionVersion, summary.DefinitionVersion);
        Assert.Equal(run.Status, summary.Status);
        Assert.Equal(run.CreatedAtUtc, summary.CreatedAtUtc);
        Assert.Equal(run.UpdatedAtUtc, summary.UpdatedAtUtc);
        Assert.Equal(run.CompletedAtUtc, summary.CompletedAtUtc);
        Assert.Equal(run.Checkpoint.Iteration, summary.Iteration);
        Assert.Equal(run.Checkpoint.NextStepIndex, summary.NextStepIndex);
        Assert.Null(summary.FailureCode);
        Assert.False(summary.IsDeleted);
    }

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace, bool includeInvokingConversation, string createOperationId, string updateOperationId)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync(createOperationId)).Definition);
        var input = new LoopDefinitionInput(
            "Runtime test loop",
            "Executes one governed inference step.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, includeInvokingConversation),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Respond", "Return a concise response to the admitted trigger prompt.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, updateOperationId, input);

        Assert.Equal("Updated", updated.Status);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private static CustomLoopInvocationOperation PendingInvocation(LoopRunInvocationInput input, string roleId)
    {
        var prompt = input.InvocationPrompt ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var requestHash = CustomLoopInvocationRequestHash.Compute(input.OperationId, input.LoopId, input.ExpectedDefinitionVersion, input.ExpectedDefinitionHash, WorkspaceActors.Cli, AgentRuntimeSurface.Cli.Id, roleId, prompt, LlmInferenceSurface.OpenAiCodex.ToString(), "test-model");
        return new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            input.OperationId,
            requestHash,
            input.LoopId,
            input.ExpectedDefinitionVersion,
            input.ExpectedDefinitionHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            roleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(prompt),
            LlmInferenceSurface.OpenAiCodex.ToString(),
            "test-model",
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            now,
            now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The invocation is pending.");
    }

    private static async Task WriteExpiredInvocationReceiptQuotaAsync(WorkspacePaths paths)
    {
        Directory.CreateDirectory(paths.CustomLoopInvocationOperationsPath);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };
        var completedAtUtc = DateTimeOffset.UtcNow.ToUniversalTime() - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration - TimeSpan.FromDays(1);
        long retainedBytes = 0;
        for (var index = 0; retainedBytes <= CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes; index++)
        {
            var operationId = $"invoke-expired-{index:D5}";
            var input = new LoopRunInvocationInput("loop-expired", 1, new string('b', CustomLoopLimits.Sha256HexCharacters), operationId, "expired quota receipt");
            var completed = PendingInvocation(input, "default") with
            {
                BindingState = CustomLoopInvocationBindingState.ConversationNotFound,
                InvokingConversationId = new string('c', CustomLoopLimits.Sha256HexCharacters),
                CreatedAtUtc = completedAtUtc.AddSeconds(-1),
                UpdatedAtUtc = completedAtUtc,
                State = CustomLoopInvocationOperationState.Complete,
                Outcome = CustomLoopInvocationOutcome.Rejected,
                AdmissionStatus = CustomLoopAdmissionStatusNames.NotFound,
                Detail = new string('d', CustomLoopLimits.MaxRunDetailCharacters)
            };
            var path = Path.Combine(paths.CustomLoopInvocationOperationsPath, operationId + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(completed, jsonOptions));
            retainedBytes += new FileInfo(path).Length;
        }
    }

    private static async Task PersistRejectedReceiptAsync(WorkspacePaths paths, LoopRunInvocationInput input, string roleId, string runId, CustomLoopAdmissionStatus admissionStatus)
    {
        var store = new CustomLoopInvocationOperationStore(paths);
        var pending = PendingInvocation(input, roleId);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var bound = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version,
            ContextIdentityHash = new string('c', CustomLoopLimits.Sha256HexCharacters),
            Detail = "The invocation context was captured before its rejected admission outcome."
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(bound)).Status);
        var completed = bound with
        {
            UpdatedAtUtc = bound.UpdatedAtUtc.AddSeconds(1),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = admissionStatus.ToString(),
            RunId = runId,
            Detail = $"The {admissionStatus} rejection retained its status-specific run relationship."
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
    }

    private static async Task<CustomLoopRunRecord> CreateReferencedRunAsync(CustomLoopRunStore store, CustomLoopDefinition definition, string runId, string admissionOperationId)
    {
        var now = DateTimeOffset.UtcNow.ToUniversalTime();
        var admittedEvent = RuntimeEvent(1, $"{runId}-admitted", CustomLoopRunEventKind.Admitted, now);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            "cli",
            new CustomLoopModelSnapshot(LlmInferenceSurface.OpenAiCodex.ToString(), "test-model"),
            admissionOperationId,
            WorkspaceActors.Cli,
            string.Empty,
            definition,
            "existing invocation",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admittedEvent],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(run)).Status);
        return run;
    }

    private static CustomLoopRunRecord AdvanceRun(CustomLoopRunRecord run, CustomLoopRunStatus status)
    {
        var updatedAt = run.UpdatedAtUtc.AddSeconds(1);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = updatedAt,
            CompletedAtUtc = status == CustomLoopRunStatus.Completed ? updatedAt : null,
            ExecutionClock = status == CustomLoopRunStatus.Running ? new CustomLoopExecutionClock(0, updatedAt) : new CustomLoopExecutionClock(1_000, null),
            Events = [.. run.Events, RuntimeEvent(run.Events.Length + 1L, $"{run.Id}-event-{run.Events.Length + 1}", CustomLoopRunEventKind.LifecycleChanged, updatedAt)],
            FinalOutput = status == CustomLoopRunStatus.Completed ? "done" : null
        };
    }

    private static CustomLoopRunEvent RuntimeEvent(long sequence, string eventId, CustomLoopRunEventKind kind, DateTimeOffset timestamp)
    {
        return new CustomLoopRunEvent(sequence, eventId, timestamp, kind, null, null, null, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);
    }

    private static bool HasValidSurrogatePairs(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(TestWorkspace workspace, IAgentRuntimeConversationPublicationObserver? observer = null)
    {
        var factory = observer is null
            ? new AgentRuntimeFactory(new RejectingApprovalPrompt())
            : new AgentRuntimeFactory(new RejectingApprovalPrompt(), observer);
        return await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            await CreateFakeCodexExecutableAsync(workspace),
            "read-only",
            AgentRuntimeSurface.Cli);
    }

    private static async Task<AgentRuntime> CreateRuntimeWithoutProviderAsync(TestWorkspace workspace)
    {
        var executable = OperatingSystem.IsWindows() ? await CreateFakeCodexExecutableAsync(workspace) : "/usr/bin/false";
        return await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync("test-model", workspace.RootPath, executable, "read-only", AgentRuntimeSurface.Cli);
    }

    private sealed class RecordingConversationPublicationObserver : IAgentRuntimeConversationPublicationObserver
    {
        public List<AgentRuntimeConversationPublication> Publications { get; } = [];

        public Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default)
        {
            Publications.Add(publication);
            return Task.CompletedTask;
        }
    }

    private static async Task WaitForAttemptStartAsync(TestWorkspace workspace)
    {
        var markerPath = workspace.File("custom-attempt-started.marker");
        var deadline = DateTime.UtcNow.Add(_providerAttemptStartTimeout);
        while (!File.Exists(markerPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(markerPath), $"The custom-loop provider attempt did not start within {_providerAttemptStartTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-custom-loop-codex.ps1");
        var commandPath = workspace.File("fake-custom-loop-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            if ($args -contains "--version") {
                Write-Output "codex-cli 999.0.0-test"
                exit 0
            }

            $threadId = "thread-test"

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json

                switch ($message.method) {
                    "initialize" {
                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "model/list" {
                        Write-ProtocolJson @{ id = $message.id; result = @{ data = @(@{ id = "test-model"; model = "test-model" }, @{ id = "gpt-test"; model = "gpt-test" }) } }
                    }

                    "thread/start" {
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
                    }

                    "turn/start" {
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $heldOnceMarker = Join-Path $PSScriptRoot "custom-attempt-held-once.marker"
                        $shouldHold = $userText.Contains("held") -and (-not $userText.Contains("held-once") -or -not (Test-Path $heldOnceMarker))
                        if ($shouldHold) {
                            if ($userText.Contains("held-once")) {
                                [IO.File]::WriteAllText($heldOnceMarker, "held")
                            }
                            [IO.File]::WriteAllText((Join-Path $PSScriptRoot "custom-attempt-started.marker"), "started")
                            $releaseMarker = Join-Path $PSScriptRoot "custom-attempt-release.marker"
                            $releaseDeadline = [DateTime]::UtcNow.AddSeconds(10)
                            while (-not (Test-Path $releaseMarker)) {
                                if ([DateTime]::UtcNow -ge $releaseDeadline) {
                                    throw "Timed out waiting for the test to release the held custom-loop attempt."
                                }
                                Start-Sleep -Milliseconds 25
                            }
                            while ($true) {
                                try {
                                    Remove-Item -LiteralPath $releaseMarker -ErrorAction Stop
                                    break
                                }
                                catch [IO.IOException] {
                                    if ([DateTime]::UtcNow -ge $releaseDeadline) {
                                        throw "Timed out consuming the test release marker for the held custom-loop attempt."
                                    }
                                    Start-Sleep -Milliseconds 25
                                }
                            }
                        }
                        elseif ($userText.Contains("delayed")) {
                            [IO.File]::WriteAllText((Join-Path $PSScriptRoot "custom-attempt-started.marker"), "started")
                            Start-Sleep -Milliseconds 1500
                        }
                        $triggerMatch = [regex]::Match($userText, '(?s)(\[EmbodySense untrusted trigger prompt data\]\r?\n.*?)\r?\n\[/restored user message\]')
                        if ($triggerMatch.Success) {
                            $userText = $triggerMatch.Groups[1].Value
                        }
                        else {
                            $currentUserMarker = "Current user message:"
                            $currentUserIndex = $userText.IndexOf($currentUserMarker)
                            if ($currentUserIndex -ge 0) {
                                $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                            }
                        }
                        $text = "fake response: $userText"

                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = $turnId; delta = $text } }
                        Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-custom-loop-codex.ps1" %*
            """);

        return commandPath;
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No tool authority is assigned in these runtime tests."));
        }
    }
}
