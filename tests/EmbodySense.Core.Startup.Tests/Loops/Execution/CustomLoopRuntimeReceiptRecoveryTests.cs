using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopRuntimeReceiptRecoveryTests
{
    [Fact]
    public async Task Pending_receipt_with_an_already_admitted_run_is_bound_and_reconciled_before_a_new_busy_owner()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            workspace.File("unused-codex.cmd"),
            "read-only",
            AgentRuntimeSurface.Cli);
        var definitionStore = new CustomLoopDefinitionStore(paths);
        var runStore = new CustomLoopRunStore(paths);
        var receiptStore = new CustomLoopInvocationOperationStore(paths);
        var definition = Assert.IsType<CustomLoopDefinition>(await definitionStore.GetAsync(definitionSnapshot.Id));
        const string OperationId = "invoke-interrupted-after-admission";
        const string Prompt = "admitted before receipt completion";
        var now = DateTimeOffset.UtcNow;
        var requestHash = CustomLoopInvocationRequestHash.Compute(
            OperationId,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            definition.RoleId,
            Prompt,
            LlmInferenceSurface.OpenAiCodex.ToString(),
            "test-model");
        var pending = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            OperationId,
            requestHash,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            definition.RoleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(Prompt),
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
            "Invocation receipt persisted before the simulated interruption.");
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await receiptStore.BeginAsync(pending)).Status);
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var conversationIdentity = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version;
        var conversation = new CustomLoopConversationReference(conversationIdentity, new string('d', CustomLoopLimits.Sha256HexCharacters), now);
        pending = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = conversationIdentity,
            ContextIdentityHash = CustomLoopContextSnapshotHash.ComputeIdentity(context)
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await receiptStore.BindAsync(pending)).Status);
        var admission = await new CustomLoopAdmissionService(definitionStore, runStore, new AuditLog(paths), new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(paths))).AdmitAsync(
            new CustomLoopAdmissionRequest(
                definition.Id,
                definition.DefinitionVersion,
                definition.ContentHash,
                OperationId,
                WorkspaceActors.Cli,
                AgentRuntimeSurface.Cli.Id,
                definition.RoleId,
                Prompt,
                new CustomLoopModelSnapshot(LlmInferenceSurface.OpenAiCodex.ToString(), "test-model"),
                conversation,
                context));
        Assert.Equal(CustomLoopAdmissionStatus.Admitted, admission.Status);
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(paths);
        var competing = competingGate.TryAcquire("competing-active-operation", new string('f', CustomLoopLimits.Sha256HexCharacters));
        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, competing.Status);
        Assert.NotNull(competing.Lease);
        using (competing.Lease)
        {
            var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, OperationId, Prompt));

            Assert.Equal("Admitted", response.AdmissionStatus);
            Assert.False(response.WasDispatched);
            Assert.Equal(admission.Run!.Id, response.Run!.Id);
            Assert.Equal(CustomLoopRunStatus.Admitted.ToString(), response.ExecutionStatus);
        }

        var completed = Assert.IsType<CustomLoopInvocationOperation>(await receiptStore.GetAsync(OperationId));
        Assert.Equal(CustomLoopInvocationOperationState.Complete, completed.State);
        Assert.Equal(CustomLoopInvocationOutcome.Admitted, completed.Outcome);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, completed.BindingState);
        Assert.Equal(conversationIdentity, completed.InvokingConversationId);
        Assert.Equal(CustomLoopContextSnapshotHash.ComputeIdentity(context), completed.ContextIdentityHash);
        Assert.Equal(admission.Run!.Id, completed.RunId);
        Assert.NotEqual(CustomLoopInvocationOutcome.WorkspaceExecutionBusy, completed.Outcome);
    }

    [Fact]
    public async Task Pending_captured_receipt_terminalizes_and_replays_busy_without_losing_its_context_binding()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            workspace.File("unused-codex.cmd"),
            "read-only",
            AgentRuntimeSurface.Cli);
        var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(definitionSnapshot.Id));
        var receiptStore = new CustomLoopInvocationOperationStore(paths);
        const string OperationId = "invoke-interrupted-after-context-capture";
        const string Prompt = "captured before workspace became busy";
        var now = DateTimeOffset.UtcNow;
        var requestHash = CustomLoopInvocationRequestHash.Compute(
            OperationId,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            definition.RoleId,
            Prompt,
            LlmInferenceSurface.OpenAiCodex.ToString(),
            "test-model");
        var pending = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            OperationId,
            requestHash,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            definition.RoleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(Prompt),
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
            "Invocation receipt persisted before the simulated interruption.");
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await receiptStore.BeginAsync(pending)).Status);
        var conversationIdentity = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version;
        var contextIdentity = CustomLoopContextSnapshotHash.ComputeIdentity(CustomLoopContextSnapshot.CreateEmpty(now));
        var captured = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = conversationIdentity,
            ContextIdentityHash = contextIdentity
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await receiptStore.BindAsync(captured)).Status);

        await using var competingGate = new CustomLoopWorkspaceExecutionGate(paths);
        using var competing = competingGate.TryAcquire("competing-after-context-capture", new string('f', CustomLoopLimits.Sha256HexCharacters)).Lease!;
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, OperationId, Prompt);
        var response = await runtime.InvokeCustomLoopAsync(input);
        var replay = await runtime.InvokeCustomLoopAsync(input);
        var completed = Assert.IsType<CustomLoopInvocationOperation>(await receiptStore.GetAsync(OperationId));

        Assert.Equal("WorkspaceExecutionBusy", response.AdmissionStatus);
        Assert.Equal("WorkspaceExecutionBusy", replay.AdmissionStatus);
        Assert.False(response.WasDispatched);
        Assert.False(replay.WasDispatched);
        Assert.Contains("captured-context binding", response.Detail, StringComparison.Ordinal);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, completed.State);
        Assert.Equal(CustomLoopInvocationOutcome.WorkspaceExecutionBusy, completed.Outcome);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, completed.BindingState);
        Assert.Equal(conversationIdentity, completed.InvokingConversationId);
        Assert.Equal(contextIdentity, completed.ContextIdentityHash);
    }

    [Fact]
    public async Task Receipt_completion_failure_after_admission_parks_the_run_and_a_later_replay_completes_without_dispatch()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            workspace.File("unused-codex.cmd"),
            "read-only",
            AgentRuntimeSurface.Cli);
        const string OperationId = "invoke-receipt-completion-failure";
        const string Prompt = "must never dispatch";
        var prepared = await PrepareInterruptedAdmissionAsync(paths, definitionSnapshot, OperationId, Prompt);
        var receiptPath = Path.Combine(paths.CustomLoopInvocationOperationsPath, OperationId + ".json");
        File.SetAttributes(receiptPath, FileAttributes.ReadOnly);

        LoopRunInvocationResponse response;
        try
        {
            response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definitionSnapshot.Id, definitionSnapshot.DefinitionVersion, definitionSnapshot.ContentHash, OperationId, Prompt));
        }
        finally
        {
            File.SetAttributes(receiptPath, FileAttributes.Normal);
        }

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), response.AdmissionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), response.ExecutionStatus);
        Assert.False(response.WasDispatched);
        Assert.Equal(prepared.Run.Id, response.Run!.Id);
        Assert.DoesNotContain(response.Run.Events, item => item.Kind is nameof(CustomLoopRunEventKind.NodeAttemptStarted) or nameof(CustomLoopRunEventKind.ExitDecisionStarted));
        Assert.Equal(CustomLoopInvocationOperationState.Pending, (await prepared.ReceiptStore.GetAsync(OperationId))!.State);

        var replay = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definitionSnapshot.Id, definitionSnapshot.DefinitionVersion, definitionSnapshot.ContentHash, OperationId, Prompt));

        Assert.Equal(CustomLoopAdmissionStatus.Admitted.ToString(), replay.AdmissionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, (await prepared.ReceiptStore.GetAsync(OperationId))!.State);
    }

    [Fact]
    public async Task Unreadable_invocation_receipt_is_reported_as_non_definitive()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        const string OperationId = "invoke-unreadable-receipt";
        Directory.CreateDirectory(paths.CustomLoopInvocationOperationsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopInvocationOperationsPath, OperationId + ".json"), "{");
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            workspace.File("unused-codex.cmd"),
            "read-only",
            AgentRuntimeSurface.Cli);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, OperationId, "retry the unresolved operation"));

        Assert.Equal(CustomLoopAdmissionStatus.ReceiptUnavailable.ToString(), response.AdmissionStatus);
        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Contains("could not be read safely", response.Detail, StringComparison.Ordinal);
    }

    private static async Task<(CustomLoopRunRecord Run, CustomLoopInvocationOperationStore ReceiptStore)> PrepareInterruptedAdmissionAsync(WorkspacePaths paths, LoopDefinitionSnapshot definitionSnapshot, string operationId, string prompt)
    {
        var definitionStore = new CustomLoopDefinitionStore(paths);
        var runStore = new CustomLoopRunStore(paths);
        var receiptStore = new CustomLoopInvocationOperationStore(paths);
        var definition = Assert.IsType<CustomLoopDefinition>(await definitionStore.GetAsync(definitionSnapshot.Id));
        var now = DateTimeOffset.UtcNow;
        var requestHash = CustomLoopInvocationRequestHash.Compute(operationId, definition.Id, definition.DefinitionVersion, definition.ContentHash, WorkspaceActors.Cli, AgentRuntimeSurface.Cli.Id, definition.RoleId, prompt, LlmInferenceSurface.OpenAiCodex.ToString(), "test-model");
        var pending = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            operationId,
            requestHash,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            definition.RoleId,
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
            "Invocation receipt persisted before the simulated interruption.");
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await receiptStore.BeginAsync(pending)).Status);
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var conversationIdentity = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version;
        var conversation = new CustomLoopConversationReference(conversationIdentity, new string('d', CustomLoopLimits.Sha256HexCharacters), now);
        pending = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = conversationIdentity,
            ContextIdentityHash = CustomLoopContextSnapshotHash.ComputeIdentity(context)
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await receiptStore.BindAsync(pending)).Status);
        var admission = await new CustomLoopAdmissionService(definitionStore, runStore, new AuditLog(paths), new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(paths))).AdmitAsync(
            new CustomLoopAdmissionRequest(
                definition.Id,
                definition.DefinitionVersion,
                definition.ContentHash,
                operationId,
                WorkspaceActors.Cli,
                AgentRuntimeSurface.Cli.Id,
                definition.RoleId,
                prompt,
                new CustomLoopModelSnapshot(LlmInferenceSurface.OpenAiCodex.ToString(), "test-model"),
                conversation,
                context));
        Assert.Equal(CustomLoopAdmissionStatus.Admitted, admission.Status);
        return (Assert.IsType<CustomLoopRunRecord>(admission.Run), receiptStore);
    }

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-receipt-recovery-loop")).Definition);
        var input = new LoopDefinitionInput(
            "Receipt recovery loop",
            "Proves interrupted invocation receipt reconciliation.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Respond", "Return one concise response.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-receipt-recovery-loop", input);
        Assert.Equal("Updated", updated.Status);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No governed tool authority is needed in this receipt recovery test."));
        }
    }

}
