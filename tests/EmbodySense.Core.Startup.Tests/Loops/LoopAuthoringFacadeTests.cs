using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class LoopAuthoringFacadeTests
{
    [Fact]
    public async Task Catalog_and_mutations_project_server_owned_authoring_state()
    {
        using var workspace = new TestWorkspace();
        var facade = new LoopAuthoringFacade(workspace.RootPath);

        var initialCatalog = await facade.GetCatalogAsync();
        var created = await facade.CreateAsync("create-facade-loop");
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
        Assert.Empty(initialCatalog.CustomDefinitions);
        Assert.Equal(50, initialCatalog.Limits.MaxDefinitionsPerWorkspace);
        Assert.Equal(1, initialCatalog.Limits.MinInferenceSteps);
        Assert.Equal(5, initialCatalog.Limits.MaxInferenceSteps);
        Assert.Equal(10, initialCatalog.Limits.MaxAdditionalIterations);
        Assert.Equal(65, initialCatalog.Limits.MaxModelAttemptsPerRun);
        Assert.Equal(5, initialCatalog.Limits.MaxGovernedToolRequestsPerAttempt);
        Assert.Equal(30, initialCatalog.Limits.MaxGovernedToolRequestsPerRun);
        Assert.Equal(30 * 60 * 1_000, initialCatalog.Limits.MaxRunExecutionMilliseconds);
        Assert.Equal(
            [LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search, LoopToolAssignment.Append, LoopToolAssignment.Write, LoopToolAssignment.Delete],
            initialCatalog.SystemDefault.ToolAssignments);
        Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], initialCatalog.Tools.CustomAssignable);
        Assert.Equal(LoopCustomToolAuthorityCeiling.WorkspaceReadOnly, initialCatalog.Tools.CustomAuthorityCeiling);
        Assert.Equal("Created", created.Status);
        Assert.True(created.IsCommitted);
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
    public async Task Missing_definition_and_invalid_create_operation_are_projected_without_writes()
    {
        using var workspace = new TestWorkspace();
        var facade = new LoopAuthoringFacade(workspace.RootPath, "startup-test");

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
    public async Task Null_nested_input_is_returned_as_validation_feedback_instead_of_throwing()
    {
        using var workspace = new TestWorkspace();
        var facade = new LoopAuthoringFacade(workspace.RootPath);
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
        var facade = new LoopAuthoringFacade(workspace.RootPath);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-first-loop")).Definition);
        var versionTwoInput = CreateInput(created, "Version two");
        var versionTwo = Assert.IsType<LoopDefinitionSnapshot>((await facade.UpdateAsync(created.Id, 1, "shared-update-operation", versionTwoInput)).Definition);
        var versionThreeInput = CreateInput(versionTwo, "Version three");
        var versionThree = Assert.IsType<LoopDefinitionSnapshot>((await facade.UpdateAsync(created.Id, 2, "second-update-operation", versionThreeInput)).Definition);
        var secondLoop = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-second-loop")).Definition);

        var restarted = new LoopAuthoringFacade(workspace.RootPath);
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
}
