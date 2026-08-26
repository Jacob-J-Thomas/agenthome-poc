using EmbodySense.Core.Startup.Loops.Models;
using System.Text.Json.Nodes;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Tests.Triggers.Schedules;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Tests.Support;
using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class LoopAuthoringFacadeTests
{
    [Fact]
    public async Task Catalog_and_mutations_project_server_owned_authoring_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var facade = CreateFacade(workspace);

        var initialCatalog = await facade.GetCatalogAsync();
        var firstSaveInput = initialCatalog.DraftTemplate.Definition with { DisplayName = "Explicit facade loop", Description = "First durable version." };
        var created = await facade.CreateAsync("create-facade-loop", firstSaveInput);
        var createdDefinition = Assert.IsType<LoopDefinitionSnapshot>(created.Definition);
        var hostileText = "Review </script><script>alert(\"owned\")</script> & keep it as data.";
        var updateInput = CreateInput(createdDefinition, hostileText);
        var updated = await facade.UpdateAsync(createdDefinition.Id, createdDefinition.DefinitionVersion, "update-facade-loop", updateInput);
        var updatedDefinition = Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
        var replayed = await facade.UpdateAsync(createdDefinition.Id, createdDefinition.DefinitionVersion, "update-facade-loop", updateInput);
        var invalid = await facade.UpdateAsync(createdDefinition.Id, updatedDefinition.DefinitionVersion, "invalid-facade-loop", updateInput with { DisplayName = " " });
        var conflict = await facade.UpdateAsync(createdDefinition.Id, createdDefinition.DefinitionVersion, "conflict-facade-loop", updateInput with { Description = "Changed elsewhere." });
        var fetched = await facade.GetAsync(createdDefinition.Id);
        var populatedCatalog = await facade.GetCatalogAsync();
        var deleted = await facade.DeleteAsync(createdDefinition.Id, updatedDefinition.DefinitionVersion, "delete-facade-loop");
        var replayedDelete = await facade.DeleteAsync(createdDefinition.Id, updatedDefinition.DefinitionVersion, "delete-facade-loop");
        var missing = await facade.GetAsync(createdDefinition.Id);

        Assert.Equal("default-assistant", initialCatalog.RoleId);
        Assert.Equal("default-conversation", initialCatalog.SystemDefault.Id);
        Assert.Equal(new ContextualRoleRevisionIdentity("default-assistant", 1), initialCatalog.SystemDefault.OwningRole.Identity);
        Assert.Equal(64, initialCatalog.SystemDefault.OwningRole.ContentHash.Length);
        Assert.Empty(initialCatalog.CustomDefinitions);
        Assert.Equal(initialCatalog.RoleId, initialCatalog.DraftTemplate.RoleId);
        Assert.Equal("Untitled loop", initialCatalog.DraftTemplate.Definition.DisplayName);
        Assert.Null(Assert.Single(initialCatalog.DraftTemplate.Definition.InferenceSteps).Id);
        Assert.Equal(LoopContextPolicyMode.Inherit, initialCatalog.DraftTemplate.Definition.InferenceSteps.Single().ContextPolicy.Mode);
        Assert.Equal(50, initialCatalog.Limits.MaxDefinitionsPerWorkspace);
        Assert.Equal(1, initialCatalog.Limits.MinInferenceSteps);
        Assert.Equal(5, initialCatalog.Limits.MaxInferenceSteps);
        Assert.Equal(10, initialCatalog.Limits.MaxAdditionalIterations);
        Assert.Equal(65, initialCatalog.Limits.MaxModelAttemptsPerRun);
        Assert.Equal(5, initialCatalog.Limits.MaxGovernedToolRequestsPerAttempt);
        Assert.Equal(30, initialCatalog.Limits.MaxGovernedToolRequestsPerRun);
        Assert.Equal(30 * 60 * 1_000, initialCatalog.Limits.MaxRunExecutionMilliseconds);
        Assert.Equal(
            [LoopCapabilityIds.ConversationTurn, LoopCapabilityIds.ConversationHistory, LoopCapabilityIds.AgentContext, LoopCapabilityIds.ProviderInference, LoopCapabilityIds.WorkspaceCommand, LoopCapabilityIds.ApprovalRequest, LoopCapabilityIds.AuditWrite],
            initialCatalog.SystemDefault.CapabilityIds);
        Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], initialCatalog.Tools.CustomAssignable);
        Assert.Equal(LoopCustomToolAuthorityCeiling.WorkspaceReadOnly, initialCatalog.Tools.CustomAuthorityCeiling);
        Assert.Equal("Created", created.Status);
        Assert.True(created.IsCommitted);
        Assert.Equal("Explicit facade loop", createdDefinition.DisplayName);
        Assert.Equal("First durable version.", createdDefinition.Description);
        Assert.Equal(1, createdDefinition.DefinitionVersion);
        Assert.NotNull(Assert.Single(createdDefinition.InferenceSteps).Id);
        Assert.Equal(initialCatalog.RoleId, createdDefinition.RoleId);
        Assert.Equal("Updated", updated.Status);
        Assert.Equal(2, updatedDefinition.DefinitionVersion);
        Assert.Equal(hostileText, updatedDefinition.DisplayName);
        Assert.Equal(LoopTriggerPromptSource.Preset, updatedDefinition.TriggerPolicy.PromptSource);
        Assert.Equal(hostileText, updatedDefinition.TriggerPolicy.PresetPrompt);
        Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], updatedDefinition.ToolAssignments);
        Assert.Equal(LoopContextPolicyMode.Custom, updatedDefinition.InferenceSteps.Single().ContextPolicy.Mode);
        Assert.NotNull(updatedDefinition.InferenceSteps.Single().ContextPolicy.CustomPolicy);
        Assert.Equal(LoopContextPolicyMode.Custom, updatedDefinition.ExitPolicy.ContextPolicy.Mode);
        Assert.Equal("Replayed", replayed.Status);
        Assert.True(replayed.IsCommitted);
        Assert.Equal("Invalid", invalid.Status);
        Assert.False(invalid.IsCommitted);
        Assert.Contains(invalid.ValidationErrors, error => error.Code == "display_name_required" && error.Field == "displayName");
        Assert.Equal("Conflict", conflict.Status);
        Assert.False(conflict.IsCommitted);
        Assert.Equal(createdDefinition.DefinitionVersion, conflict.Conflict!.ExpectedDefinitionVersion);
        Assert.Equal(updatedDefinition.DefinitionVersion, conflict.Conflict.ActualDefinitionVersion);
        Assert.Equal(updatedDefinition.ContentHash, fetched!.ContentHash);
        Assert.Equal(updatedDefinition.ContentHash, Assert.Single(populatedCatalog.CustomDefinitions).ContentHash);
        Assert.Equal("Deleted", deleted.Status);
        Assert.True(deleted.IsCommitted);
        Assert.Equal("Replayed", replayedDelete.Status);
        Assert.True(replayedDelete.IsCommitted);
        Assert.Null(missing);
    }

    [Fact]
    public async Task System_default_projection_matches_the_canonical_graph_and_dedicated_runner_contract()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var projection = (await CreateFacade(workspace).GetCatalogAsync()).SystemDefault;
        var canonical = LoopDefinition.CreateDefaultConversation();

        Assert.Equal(canonical.SchemaVersion, projection.SchemaVersion);
        Assert.Equal(canonical.Id, projection.Id);
        Assert.Equal(canonical.DisplayName, projection.DisplayName);
        Assert.Equal(canonical.Description, projection.Description);
        Assert.Equal(canonical.RoleId, projection.OwningRole.Identity.RoleId);
        Assert.Equal(new ContextualRoleRevisionIdentity(canonical.RoleId, 1), projection.OwningRole.Identity);
        Assert.Equal(64, projection.OwningRole.ContentHash.Length);
        Assert.Equal(canonical.Trigger, projection.Trigger);
        Assert.Equal(canonical.MemoryScope, projection.MemoryScope);
        Assert.Equal(canonical.CapabilityIds, projection.CapabilityIds);
        Assert.Equal(canonical.ReviewPolicy, projection.ReviewPolicy);
        Assert.Equal(canonical.FailurePolicy, projection.FailurePolicy);
        Assert.Equal(canonical.State, projection.State);
        Assert.Equal(canonical.EditMode, projection.EditMode);
        Assert.Equal(canonical.Graph.EntryNodeId, projection.Graph.EntryNodeId);
        Assert.Equal(canonical.Graph.TerminalNodeIds, projection.Graph.TerminalNodeIds);
        Assert.Equal(canonical.Graph.Nodes.Length, projection.Graph.Nodes.Count);
        for (var index = 0; index < canonical.Graph.Nodes.Length; index++)
        {
            var expected = canonical.Graph.Nodes[index];
            var actual = projection.Graph.Nodes[index];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.EditMode, actual.EditMode);
            Assert.Equal(expected.CapabilityIds, actual.CapabilityIds);
            Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, actual.ExecutionSemantics);
        }

        Assert.Equal(canonical.Graph.Edges.Length, projection.Graph.Edges.Count);
        for (var index = 0; index < canonical.Graph.Edges.Length; index++)
        {
            var expected = canonical.Graph.Edges[index];
            var actual = projection.Graph.Edges[index];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.FromNodeId, actual.FromNodeId);
            Assert.Equal(expected.ToNodeId, actual.ToNodeId);
            Assert.Equal(expected.Condition, actual.Condition);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, actual.ExecutionSemantics);
        }

        Assert.Equal("DefaultConversationLoopRunner", projection.ExecutionContract.Runner);
        Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, projection.ExecutionContract.GraphSemantics);
        Assert.False(projection.ExecutionContract.UsesGenericGraphDispatcher);
        Assert.Contains("does not certify", projection.ExecutionContract.Detail, StringComparison.Ordinal);
        Assert.Contains("assembles context before durable user acceptance", projection.ExecutionContract.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(projection.Graph.Nodes, node => node.Id is "trigger" or "exit");
    }

    [Fact]
    public async Task Structurally_valid_noncanonical_system_graph_is_not_labeled_runner_compatible()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var canonical = LoopDefinition.CreateDefaultConversation();
        const string AlternateEntryNodeId = "alternate-entry";
        var noncanonicalGraph = canonical.Graph with
        {
            EntryNodeId = AlternateEntryNodeId,
            Nodes = canonical.Graph.Nodes.Select(node => node.Id == DefaultConversationLoopGraphIds.AcceptUserMessage ? node with { Id = AlternateEntryNodeId } : node).ToArray(),
            Edges = canonical.Graph.Edges.Select(edge => edge.FromNodeId == DefaultConversationLoopGraphIds.AcceptUserMessage ? edge with { FromNodeId = AlternateEntryNodeId } : edge).ToArray()
        };
        await new LoopDefinitionStore(paths).SaveAsync(canonical with { Graph = noncanonicalGraph });

        var projection = (await CreateFacade(workspace).GetCatalogAsync()).SystemDefault;

        Assert.Equal(SystemLoopExecutionSemantics.Unknown, projection.ExecutionContract.GraphSemantics);
        Assert.All(projection.Graph.Nodes, node => Assert.Equal(SystemLoopExecutionSemantics.Unknown, node.ExecutionSemantics));
        Assert.All(projection.Graph.Edges, edge => Assert.Equal(SystemLoopExecutionSemantics.Unknown, edge.ExecutionSemantics));
        Assert.Contains("rejects this persisted graph contract", projection.ExecutionContract.Detail, StringComparison.Ordinal);
        Assert.Contains(DefaultConversationLoopGraphIds.AcceptUserMessage, projection.ExecutionContract.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_definition_and_invalid_create_operation_are_projected_without_writes()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var facade = CreateFacade(workspace, "startup-test");

        var invalidCreate = await facade.CreateAsync("INVALID OPERATION");
        var missingUpdate = await facade.UpdateAsync("missing-loop", 1, "update-missing-loop", CreateInput(null, "Valid text"));
        var missingDelete = await facade.DeleteAsync("missing-loop", 1, "delete-missing-loop");

        Assert.Equal("Invalid", invalidCreate.Status);
        Assert.Contains(invalidCreate.ValidationErrors, error => error.Code == "invalid_mutation_operation_id");
        Assert.Equal("NotFound", missingUpdate.Status);
        Assert.Equal("NotFound", missingDelete.Status);
        Assert.Empty((await facade.GetCatalogAsync()).CustomDefinitions);
    }

    [Fact]
    public async Task Missing_system_authority_in_initialized_workspace_fails_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        File.Delete(new WorkspacePaths(workspace.RootPath).DefaultConversationLoopDefinitionPath);
        var facade = CreateFacade(workspace);

        var catalogException = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.GetCatalogAsync());
        var mutationException = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.CreateAsync("blocked-create"));

        Assert.Contains("missing its default conversation authority", catalogException.Message, StringComparison.Ordinal);
        Assert.Contains("failed closed", mutationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Substituted_system_authority_identity_fails_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var path = new WorkspacePaths(workspace.RootPath).DefaultConversationLoopDefinitionPath;
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["id"] = "substituted-authority";
        await File.WriteAllTextAsync(path, root.ToJsonString());
        var facade = CreateFacade(workspace);

        var catalogException = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.GetCatalogAsync());
        var mutationException = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.CreateAsync("blocked-create"));

        Assert.Contains("substituted identity", catalogException.Message, StringComparison.Ordinal);
        Assert.Contains("failed closed", mutationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_default_role_is_not_inferred_from_role_markdown_or_backfilled_by_projection()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.Delete(Path.Combine(paths.AgentPath, "contextual-roles"), recursive: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).GetCatalogAsync());

        Assert.Contains("exact active default contextual-role lifecycle", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(paths.RolePath));
        Assert.False(Directory.Exists(Path.Combine(paths.AgentPath, "contextual-roles")));
    }

    [Fact]
    public async Task Inactive_default_role_fails_closed_before_authoring_or_projection()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var identity = new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision);
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "disable-default-assistant",
            string.Empty,
            ContextualRoleRevisionMutationKind.Disable,
            identity.RoleId,
            "startup-test",
            null,
            identity,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        using (var store = new ContextualRoleRevisionStore(paths, workspaceId))
        {
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).CreateAsync("blocked-create"));

        Assert.Contains("exact active default contextual-role lifecycle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_ambiguous_default_role_source_fails_closed_without_changing_the_persisted_pin()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var identity = new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision);
        ContextualRoleRevision before;
        using (var store = new ContextualRoleRevisionStore(paths, workspaceId))
        {
            before = Assert.IsType<ContextualRoleRevision>((await store.ReadAsync(new ContextualRoleRevisionReadRequest(identity))).Revision);
        }

        await File.WriteAllTextAsync(paths.RolePath, "   \n");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).GetCatalogAsync());

        Assert.Contains("instruction source is unavailable or substituted", exception.Message, StringComparison.Ordinal);
        using var confirmedStore = new ContextualRoleRevisionStore(paths, workspaceId);
        var after = await confirmedStore.ReadAsync(new ContextualRoleRevisionReadRequest(identity));
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, after.Status);
        Assert.Equal(before.ContentHash, after.Revision!.ContentHash);
    }

    [Fact]
    public async Task Corrupt_default_role_hash_evidence_fails_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var revisionPath = Path.Combine(paths.AgentPath, "contextual-roles", "revisions", "default-assistant.1.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(revisionPath))!.AsObject();
        root["integrityHash"] = new string('0', 64);
        await File.WriteAllTextAsync(revisionPath, root.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).GetCatalogAsync());

        Assert.Contains("failed closed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_role_bound_to_another_workspace_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PersistSystemDefinitionAndRoleAsync(
            paths,
            "workspace-sha256:" + new string('1', 64),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                "role",
                ContextualRoleInstructionClassification.RoleInstruction));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).GetCatalogAsync());

        Assert.Contains("exact published default contextual-role revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_role_with_a_substituted_source_contract_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await PersistSystemDefinitionAndRoleAsync(
            paths,
            CapabilityWorkspaceScopeId.Create(paths.RootPath),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.RoleArtifact,
                "substituted-role",
                ContextualRoleInstructionClassification.RoleInstruction));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFacade(workspace).GetCatalogAsync());

        Assert.Contains("exact published default contextual-role revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_nested_input_is_returned_as_validation_feedback_instead_of_throwing()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var facade = CreateFacade(workspace);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-null-shapes")).Definition);
        var valid = CreateInput(created, "Valid text");
        var contextWithMissingInput = new LoopContextPolicy(null!, new LoopContextOutputPolicy(true, false));
        var cases = new (string OperationId, LoopDefinitionInput Input, string ErrorCode)[]
        {
            ("null-trigger", valid with { TriggerPolicy = null! }, "trigger_policy_required"),
            ("null-step-list", valid with { InferenceSteps = null! }, "inference_steps_required"),
            ("null-step", valid with { InferenceSteps = [null!] }, "inference_step_required"),
            ("null-step-context", valid with { InferenceSteps = [valid.InferenceSteps.Single() with { ContextPolicy = null! }] }, "node_context_policy_required"),
            ("null-context-input", valid with { InferenceSteps = [valid.InferenceSteps.Single() with { ContextPolicy = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, contextWithMissingInput) }] }, "context_in_required"),
            ("null-tools", valid with { ToolAssignments = null! }, "tool_assignments_required"),
            ("null-exit", valid with { ExitPolicy = null! }, "exit_policy_required")
        };

        foreach (var testCase in cases)
        {
            var response = await facade.UpdateAsync(created.Id, created.DefinitionVersion, testCase.OperationId, testCase.Input);

            Assert.Equal("Invalid", response.Status);
            Assert.False(response.IsCommitted);
            Assert.Contains(response.ValidationErrors, error => error.Code == testCase.ErrorCode);
        }

        Assert.Equal(created.ContentHash, (await facade.GetAsync(created.Id))!.ContentHash);
    }

    [Fact]
    public async Task Update_operation_replays_its_original_snapshot_and_conflicts_on_cross_request_reuse_after_restart()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var facade = CreateFacade(workspace);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-first-loop")).Definition);
        var versionTwoInput = CreateInput(created, "Version two");
        var versionTwo = Assert.IsType<LoopDefinitionSnapshot>((await facade.UpdateAsync(created.Id, 1, "shared-update-operation", versionTwoInput)).Definition);
        var versionThreeInput = CreateInput(versionTwo, "Version three");
        var versionThree = Assert.IsType<LoopDefinitionSnapshot>((await facade.UpdateAsync(created.Id, 2, "second-update-operation", versionThreeInput)).Definition);
        var secondLoop = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-second-loop")).Definition);

        var restarted = CreateFacade(workspace);
        var replay = await restarted.UpdateAsync(created.Id, 1, "shared-update-operation", versionTwoInput);
        var changedRequest = await restarted.UpdateAsync(created.Id, 1, "shared-update-operation", versionTwoInput with { Description = "Different content" });
        var crossKind = await restarted.DeleteAsync(created.Id, versionThree.DefinitionVersion, "shared-update-operation");
        var crossLoop = await restarted.UpdateAsync(secondLoop.Id, 1, "shared-update-operation", CreateInput(secondLoop, "Other loop"));

        Assert.Equal("Replayed", replay.Status);
        Assert.Equal(versionTwo.ContentHash, replay.Definition!.ContentHash);
        Assert.Equal(versionThree.ContentHash, (await restarted.GetAsync(created.Id))!.ContentHash);
        Assert.Equal("Conflict", changedRequest.Status);
        Assert.Equal("Conflict", crossKind.Status);
        Assert.Equal("Conflict", crossLoop.Status);
        Assert.Equal(secondLoop.ContentHash, (await restarted.GetAsync(secondLoop.Id))!.ContentHash);
    }

    [Fact]
    public async Task Delete_observes_a_just_committed_nonterminal_run_through_the_exact_borrowed_store_without_disposing_it()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new TrackingCustomLoopRunStore(new CustomLoopRunStore(paths));

        try
        {
            var facade = new LoopAuthoringFacade(workspace.RootPath, runStore);
            var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-active-loop")).Definition);
            var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(created.Id));
            var run = CreateAdmittedRun(definition);

            Assert.Equal(CustomLoopRunStoreStatus.Created, (await runStore.CreateAsync(run)).Status);
            Assert.Equal(1, runStore.CreateCallCount);
            Assert.Equal(0, runStore.GetNonterminalByLoopCallCount);

            var blocked = await facade.DeleteAsync(created.Id, created.DefinitionVersion, "delete-active-loop");

            Assert.Equal("ActiveRunExists", blocked.Status);
            Assert.Equal(1, runStore.GetNonterminalByLoopCallCount);
            Assert.Equal(created.Id, runStore.LastNonterminalLoopId);
            Assert.False(runStore.IsDisposed);
            Assert.Equal(0, runStore.DisposeCount);
            Assert.Equal(0, runStore.InnerDisposeCount);
            var retained = await runStore.GetNonterminalByLoopAsync(created.Id);
            Assert.NotNull(retained);
            Assert.Equal(run.Id, retained.Id);
        }
        finally
        {
            runStore.Dispose();
        }

        Assert.True(runStore.IsDisposed);
        Assert.Equal(1, runStore.DisposeCount);
        Assert.Equal(1, runStore.InnerDisposeCount);
    }

    private static LoopDefinitionInput CreateInput(LoopDefinitionSnapshot? definition, string text)
    {
        var context = new LoopContextPolicy(
            new LoopContextInputPolicy(true, true, false, true, true),
            new LoopContextOutputPolicy(true, false));
        return new LoopDefinitionInput(
            text,
            text,
            new LoopTriggerPolicy(LoopTriggerPromptSource.Preset, text, false),
            [new LoopInferenceStep(definition?.InferenceSteps.Single().Id, "Inspect", text, new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, context))],
            [LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search],
            new LoopExitPolicy(2, text, new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, context)));
    }

    private static CustomLoopRunRecord CreateAdmittedRun(CustomLoopDefinition definition)
    {
        var now = DateTimeOffset.UtcNow;
        CustomLoopRunEvent[] events = [new(1, "admitted-active-loop", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null)];
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-active-loop",
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "admit-active-loop",
            WorkspaceActors.Web,
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            events,
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static LoopAuthoringFacade CreateFacade(TestWorkspace workspace, string actor = WorkspaceActors.Web)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        return new LoopAuthoringFacade(workspace.RootPath, new CustomLoopRunStore(paths), actor);
    }

    private static async Task PersistSystemDefinitionAndRoleAsync(
        WorkspacePaths paths,
        string applicableWorkspaceId,
        ContextualRoleInstructionSourceReference source)
    {
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.RolePath, "# Workspace role\n");
        await new LoopDefinitionStore(paths).SaveAsync(LoopDefinition.CreateDefaultConversation());
        var actualWorkspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var timestamp = DateTimeOffset.UnixEpoch;
        var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision),
            string.Empty,
            "Default assistant",
            "Provide the workspace's bounded default conversation assistance.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("embodysense-initializer", timestamp, timestamp),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(applicableWorkspaceId)),
            source,
            new ContextualRolePolicyMaxima(
                LoopCapabilityRequirements.GetAssignedCapabilityIds(LoopCapabilityRequirements.CreateDefaultConversationManifest())
                    .Select(capabilityId => capabilityId.Value)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray())));
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "seed-default-assistant-v1",
            string.Empty,
            ContextualRoleRevisionMutationKind.Create,
            DefaultContextualRoleSeeder.RoleId,
            "embodysense-initializer",
            revision,
            null,
            timestamp));
        using var store = new ContextualRoleRevisionStore(paths, actualWorkspaceId);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }
}
