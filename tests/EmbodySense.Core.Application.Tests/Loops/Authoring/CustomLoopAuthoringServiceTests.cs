using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Authoring;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Authoring;

public sealed class CustomLoopAuthoringServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_requires_store_and_audit_log()
    {
        Assert.Throws<ArgumentNullException>(() => new CustomLoopAuthoringService(null!, new RecordingAuditLog()));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopAuthoringService(new FakeStore(), null!));
    }

    [Fact]
    public void Default_identity_generator_creates_filename_safe_unique_artifact_ids()
    {
        var identity = new CustomLoopDefinitionIdentityGenerator();

        var firstLoop = identity.NewLoopId();
        var secondLoop = identity.NewLoopId();
        var firstStep = identity.NewInferenceStepId();
        var secondStep = identity.NewInferenceStepId();

        Assert.StartsWith("loop-", firstLoop, StringComparison.Ordinal);
        Assert.StartsWith("step-", firstStep, StringComparison.Ordinal);
        Assert.NotEqual(firstLoop, secondLoop);
        Assert.NotEqual(firstStep, secondStep);
        Assert.All(new[] { firstLoop, secondLoop, firstStep, secondStep }, id => Assert.Matches("^[a-z0-9-]+$", id));
    }

    [Fact]
    public async Task List_and_get_project_only_definitions_bound_to_the_callers_role()
    {
        var definition = Definition();
        var otherRole = Rehash(definition with { Id = "loop-other", RoleId = "role-other" });
        var store = new FakeStore(definition, otherRole);
        var service = Service(store);

        var listed = await service.ListAsync(definition.RoleId);
        var loaded = await service.GetAsync(definition.Id, definition.RoleId);
        var hidden = await service.GetAsync(otherRole.Id, definition.RoleId);
        var missing = await service.GetAsync("loop-missing", definition.RoleId);

        Assert.Same(definition, Assert.Single(listed));
        Assert.Same(definition, loaded);
        Assert.Null(hidden);
        Assert.Null(missing);
        Assert.Equal(1, store.ListCallCount);
        Assert.Equal(3, store.GetCallCount);
        Assert.Equal(0, store.MutationCallCount);
    }

    [Fact]
    public async Task Create_builds_a_valid_seed_with_server_owned_identity_defaults_and_audit_trace()
    {
        var store = new FakeStore();
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator(["loop-created"], ["step-created"]);
        var service = Service(store, audit, identity);

        var result = await service.CreateAsync("role-workspace", "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Created, result.Status);
        Assert.True(result.IsCommitted);
        var definition = Assert.IsType<CustomLoopDefinition>(result.Definition);
        Assert.Same(definition, store.CreatedDefinition);
        Assert.Equal("loop-created", definition.Id);
        Assert.Equal("role-workspace", definition.RoleId);
        Assert.Equal("op-create", definition.LastMutationOperationId);
        Assert.Equal(1, definition.DefinitionVersion);
        Assert.Equal(Now, definition.CreatedAtUtc);
        Assert.Equal(Now, definition.UpdatedAtUtc);
        Assert.Equal(CustomLoopContextDefaults.CreatePrototypeDefaults(), definition.ContextDefaults);
        Assert.Equal("step-created", Assert.Single(definition.InferenceSteps).Id);
        Assert.True(CustomLoopDefinitionContentHash.Matches(definition));
        Assert.True(CustomLoopDefinitionValidator.Validate(definition).IsValid);
        Assert.Collection(
            audit.Events,
            intent => AssertAudit(intent, AuditSchema.Actions.LoopDefinitionMutationIntent, AuditSchema.Outcomes.Requested, "create", definition),
            outcome => AssertAudit(outcome, AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditSchema.Outcomes.Succeeded, "create", definition));
    }

    [Fact]
    public async Task Create_replays_an_existing_operation_without_allocating_or_mutating_again()
    {
        var existing = Definition(operationId: "op-create");
        var store = new FakeStore(existing);
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator([], []);
        var service = Service(store, audit, identity);

        var result = await service.CreateAsync(existing.RoleId, "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Replayed, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Same(existing, result.Definition);
        Assert.Equal(0, store.MutationCallCount);
        Assert.Empty(audit.Events);
        Assert.Equal(0, identity.CallCount);
    }

    [Fact]
    public async Task Create_finishes_a_pending_definition_commit_and_completes_its_audit_integrity_marker()
    {
        var existing = Definition(operationId: "op-create");
        var store = new FakeStore
        {
            CreateOperationLookupResult = CustomLoopCreateOperationLookupResult.PendingDefinitionCommit(existing),
            CreateResult = CustomLoopDefinitionStoreResult.Created(existing, CustomLoopOperationIntegrity.PendingOutcomeAudit)
        };
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator([], []);
        var service = Service(store, audit, identity);

        var result = await service.CreateAsync(existing.RoleId, "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Created, result.Status);
        Assert.Same(existing, result.Definition);
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.AuditMarkCallCount);
        Assert.Equal(0, identity.CallCount);
        var outcome = Assert.Single(audit.Events);
        AssertAudit(outcome, AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditSchema.Outcomes.Succeeded, "create", existing);
    }

    [Fact]
    public async Task Create_replay_heals_a_committed_pending_outcome_audit_without_recreating_the_definition()
    {
        var existing = Definition(operationId: "op-create");
        var store = new FakeStore
        {
            CreateOperationLookupResult = CustomLoopCreateOperationLookupResult.Committed(existing, CustomLoopOperationIntegrity.PendingOutcomeAudit)
        };
        var audit = new RecordingAuditLog();
        var service = Service(store, audit, new QueueIdentityGenerator([], []));

        var result = await service.CreateAsync(existing.RoleId, "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Replayed, result.Status);
        Assert.Equal(0, store.CreateCallCount);
        Assert.Equal(1, store.AuditMarkCallCount);
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task Create_surfaces_a_warning_when_the_outcome_audit_marker_cannot_be_completed()
    {
        var store = new FakeStore { AuditMarkResult = CustomLoopOperationAuditMarkStatus.NotFound };
        var service = Service(store);

        var result = await service.CreateAsync("role-workspace", "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.CommittedWithAuditWarning, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Contains("integrity marker", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_does_not_replay_an_operation_across_role_boundaries()
    {
        var existing = Definition(operationId: "op-create");
        var store = new FakeStore(existing);
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator([], []);
        var service = Service(store, audit, identity);

        var result = await service.CreateAsync("role-other", "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Null(result.Definition);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("role_binding_mismatch", error.Code);
        Assert.Equal(0, store.MutationCallCount);
        AssertRejectionAudit(audit, "role_binding_mismatch");
        Assert.Equal(0, identity.CallCount);
    }

    [Fact]
    public async Task Create_rejects_invalid_server_metadata_before_audit_or_storage()
    {
        var store = new FakeStore();
        var audit = new RecordingAuditLog();
        var service = Service(store, audit, new QueueIdentityGenerator(["INVALID LOOP"], ["step-created"]));

        var result = await service.CreateAsync("role-workspace", "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "invalid_artifact_id" && error.Field == "id");
        Assert.Equal(0, store.MutationCallCount);
        AssertRejectionAudit(audit, "validation_rejected");
    }

    [Fact]
    public async Task Create_maps_the_workspace_definition_limit()
    {
        var store = new FakeStore { CreateResult = CustomLoopDefinitionStoreResult.LimitExceeded() };
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.CreateAsync("role-workspace", "op-create", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.LimitExceeded, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(AuditSchema.Actions.LoopDefinitionMutationIntent, audit.Events[0].Action);
        Assert.Equal(AuditSchema.Outcomes.Denied, audit.Events[1].Outcome);
    }

    [Fact]
    public async Task Update_returns_not_found_without_audit_or_store_mutation_when_the_definition_is_absent()
    {
        var store = new FakeStore();
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.UpdateAsync("loop-missing", 1, "role-workspace", "op-update", "actor-user", Input());

        Assert.Equal(CustomLoopAuthoringStatus.NotFound, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "not_found");
    }

    [Fact]
    public async Task Update_and_Delete_reject_invalid_expected_versions_before_audit_or_storage()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var zeroUpdate = await service.UpdateAsync(current.Id, 0, current.RoleId, "op-update-zero", "actor-user", Input(current));
        var overflowUpdate = await service.UpdateAsync(current.Id, int.MaxValue, current.RoleId, "op-update-overflow", "actor-user", Input(current));
        var zeroDelete = await service.DeleteAsync(current.Id, 0, current.RoleId, "op-delete-zero", "actor-user");

        Assert.All(new[] { zeroUpdate, overflowUpdate, zeroDelete }, result =>
        {
            Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
            var error = Assert.Single(result.ValidationErrors);
            Assert.Equal("invalid_expected_definition_version", error.Code);
            Assert.Equal("expectedDefinitionVersion", error.Field);
        });
        Assert.Equal(0, store.MutationCallCount);
        Assert.Equal(0, store.MutationOperationLookupCallCount);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Update_and_Delete_reject_unsafe_loop_ids_before_store_reads()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var update = await service.UpdateAsync("../escape", 1, current.RoleId, "op-update", "actor-user", Input(current));
        var delete = await service.DeleteAsync("../escape", 1, current.RoleId, "op-delete", "actor-user");

        Assert.All(new[] { update, delete }, result =>
        {
            Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
            var error = Assert.Single(result.ValidationErrors);
            Assert.Equal("invalid_loop_id", error.Code);
            Assert.Equal("loopId", error.Field);
        });
        Assert.Equal(0, store.MutationOperationLookupCallCount);
        Assert.Equal(0, store.GetCallCount);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Update_rejects_a_role_binding_mismatch_before_audit_or_storage()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, "role-other", "op-update", "actor-user", Input(current));

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("role_binding_mismatch", error.Code);
        Assert.Equal("roleId", error.Field);
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "role_binding_mismatch");
    }

    [Fact]
    public async Task Update_validates_content_before_audit_or_storage()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);
        var invalid = Input(current) with { DisplayName = "   ", ToolAssignments = [CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Read] };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", invalid, [CustomLoopToolAssignment.Read]);

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "display_name_required");
        Assert.Contains(result.ValidationErrors, error => error.Code == "duplicate_tool_assignment");
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "validation_rejected");
    }

    [Fact]
    public async Task Update_reports_an_unsupported_assignment_before_the_current_role_ceiling()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);
        var input = Input(current) with { ToolAssignments = [(CustomLoopToolAssignment)999] };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-unsupported-tool", "actor-user", input);

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("unsupported_tool_assignment", error.Code);
        Assert.Equal("toolAssignments[0]", error.Field);
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "validation_rejected");
    }

    [Fact]
    public async Task Update_simple_overload_grants_no_implicit_tool_authority()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var input = Input(current) with { ToolAssignments = [CustomLoopToolAssignment.Read] };

        var result = await Service(store).UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", input);

        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("tool_assignment_outside_role_ceiling", error.Code);
        Assert.Equal(0, store.UpdateCallCount);
    }

    [Fact]
    public async Task Update_preserves_existing_step_ids_allocates_new_ids_and_cannot_replace_context_defaults()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator([], ["step-new"]);
        var service = Service(store, audit, identity);
        var input = Input(current) with
        {
            InferenceSteps =
            [
                new CustomLoopInferenceStepInput(current.InferenceSteps[0].Id, "Stable step", "Updated instruction", CustomLoopNodeContextPolicy.Inherit()),
                new CustomLoopInferenceStepInput(null, "New step", "Second instruction", CustomLoopNodeContextPolicy.Override(Policy()))
            ],
            ToolAssignments = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read]
        };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", input, [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read]);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, result.Status);
        var updated = Assert.IsType<CustomLoopDefinition>(result.Definition);
        Assert.Equal(current.DefinitionVersion + 1, updated.DefinitionVersion);
        Assert.Equal("op-update", updated.LastMutationOperationId);
        Assert.Equal(Now, updated.UpdatedAtUtc);
        Assert.Equal([current.InferenceSteps[0].Id, "step-new"], updated.InferenceSteps.Select(step => step.Id));
        Assert.Same(current.ContextDefaults, updated.ContextDefaults);
        Assert.Equal(CustomLoopContextDefaults.CreatePrototypeDefaults(), updated.ContextDefaults);
        Assert.True(CustomLoopDefinitionContentHash.Matches(updated));
        Assert.Equal(1, identity.CallCount);
        Assert.Collection(
            audit.Events,
            intent => AssertAudit(intent, AuditSchema.Actions.LoopDefinitionMutationIntent, AuditSchema.Outcomes.Requested, "update", updated),
            outcome => AssertAudit(outcome, AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditSchema.Outcomes.Succeeded, "update", updated));
    }

    [Fact]
    public async Task Update_rejects_a_persisted_definition_with_tampered_server_owned_context_defaults()
    {
        var tamperedDefaults = new CustomLoopContextDefaults(
            Policy(role: false, trigger: false, conversation: true, retained: false, previous: false, retainOutput: false, publish: true),
            Policy(role: true, trigger: false, conversation: true, retained: true, previous: false, retainOutput: true, publish: false));
        var current = Rehash(Definition() with { ContextDefaults = tamperedDefaults });
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", Input(current));

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "server_context_defaults_changed");
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "server_context_defaults_changed");
    }

    [Fact]
    public async Task Update_rejects_unknown_and_duplicate_step_ids()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);
        var input = Input(current) with
        {
            InferenceSteps =
            [
                new CustomLoopInferenceStepInput("step-unknown", "Unknown", "Instruction", CustomLoopNodeContextPolicy.Inherit()),
                new CustomLoopInferenceStepInput(current.InferenceSteps[0].Id, "First", "Instruction", CustomLoopNodeContextPolicy.Inherit()),
                new CustomLoopInferenceStepInput(current.InferenceSteps[0].Id, "Duplicate", "Instruction", CustomLoopNodeContextPolicy.Inherit())
            ]
        };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "unknown_inference_step_id");
        Assert.Contains(result.ValidationErrors, error => error.Code == "duplicate_inference_step_id");
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "validation_rejected");
    }

    [Fact]
    public async Task Update_reports_missing_and_null_inference_step_entries_as_validation_errors()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var missing = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-missing-steps", "actor-user", Input(current) with { InferenceSteps = null! });
        var nullEntry = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-null-step", "actor-user", Input(current) with { InferenceSteps = [null!] });

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, missing.Status);
        Assert.Contains(missing.ValidationErrors, error => error.Code == "inference_steps_required");
        Assert.Equal(CustomLoopAuthoringStatus.Invalid, nullEntry.Status);
        Assert.Contains(nullEntry.ValidationErrors, error => error.Code == "inference_step_required" && error.Field == "inferenceSteps[0]");
        Assert.Equal(0, store.UpdateCallCount);
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, item => Assert.Equal("validation_rejected", item.Metadata["rejection_code"]));
    }

    [Fact]
    public async Task Update_maps_a_concurrent_version_conflict_with_current_evidence()
    {
        var current = Definition(version: 2);
        var store = new FakeStore(current) { UpdateResult = CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion: 1) };
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.UpdateAsync(current.Id, 1, current.RoleId, "op-update", "actor-user", Input(current));

        Assert.Equal(CustomLoopAuthoringStatus.Conflict, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Null(result.Definition);
        Assert.Equal(current.Id, result.Conflict?.LoopId);
        Assert.Equal(1, result.Conflict?.ExpectedDefinitionVersion);
        Assert.Equal(2, result.Conflict?.ActualDefinitionVersion);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(1, store.UpdatedExpectedVersion);
        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(AuditSchema.Outcomes.Conflict, audit.Events[1].Outcome);
    }

    [Fact]
    public async Task Update_maps_a_store_not_found_race_after_admission()
    {
        var current = Definition();
        var store = new FakeStore(current) { UpdateResult = CustomLoopDefinitionStoreResult.NotFound() };
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", Input(current));

        Assert.Equal(CustomLoopAuthoringStatus.NotFound, result.Status);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(AuditSchema.Outcomes.NotFound, audit.Events[1].Outcome);
    }

    [Fact]
    public async Task Update_replays_the_same_operation_and_content_without_allocating_a_replacement_step_id()
    {
        var current = Rehash(Definition(operationId: "op-update", version: 2) with
        {
            InferenceSteps = [new CustomLoopInferenceStep("step-generated", "Existing", "Instruction", CustomLoopNodeContextPolicy.Inherit())]
        });
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator([], []);
        var service = Service(store, audit, identity);
        var input = Input(current) with
        {
            InferenceSteps = [new CustomLoopInferenceStepInput(null, "Existing", "Instruction", CustomLoopNodeContextPolicy.Inherit())]
        };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion - 1, current.RoleId, "op-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Replayed, result.Status);
        Assert.Same(current, result.Definition);
        Assert.Equal(0, identity.CallCount);
        Assert.Equal(0, store.UpdateCallCount);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Update_rejects_operation_id_reuse_with_different_content_without_mutation()
    {
        var current = Definition(operationId: "op-update");
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);
        var different = Input(current) with { DisplayName = "Different content" };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", different);

        Assert.Equal(CustomLoopAuthoringStatus.Conflict, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Same(current, result.Definition);
        Assert.Null(result.Conflict);
        Assert.Contains("reused with different content", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, store.UpdateCallCount);
        AssertRejectionAudit(audit, "operation_reuse_conflict");
    }

    [Fact]
    public async Task Update_operation_reuse_conflict_does_not_disclose_another_roles_definition()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var service = Service(store);
        var input = Input(current) with { Description = "role-private content" };
        var first = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "shared-operation", "actor-user", input);

        var conflict = await service.UpdateAsync(current.Id, current.DefinitionVersion, "role-other", "shared-operation", "actor-other", input);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, first.Status);
        Assert.Equal(CustomLoopAuthoringStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Definition);
        Assert.Equal(1, store.UpdateCallCount);
    }

    [Fact]
    public async Task Update_rejects_operation_id_reuse_when_only_step_content_changed()
    {
        var current = Definition(operationId: "op-update");
        var store = new FakeStore(current);
        var service = Service(store);
        var different = Input(current) with
        {
            InferenceSteps = [new CustomLoopInferenceStepInput(current.InferenceSteps[0].Id, "Changed step", current.InferenceSteps[0].Instruction, current.InferenceSteps[0].ContextPolicy)]
        };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", different);

        Assert.Equal(CustomLoopAuthoringStatus.Conflict, result.Status);
        Assert.Equal(0, store.UpdateCallCount);
    }

    [Fact]
    public async Task Update_treats_malformed_reused_operation_content_as_a_conflict_instead_of_throwing()
    {
        var current = Definition(operationId: "op-update");
        var store = new FakeStore(current);
        var service = Service(store);
        var malformed = Input(current) with { InferenceSteps = null!, ToolAssignments = null! };

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", malformed);

        Assert.Equal(CustomLoopAuthoringStatus.Conflict, result.Status);
        Assert.Equal(0, store.UpdateCallCount);
    }

    [Fact]
    public async Task Durable_Update_operation_replays_its_original_result_after_a_later_update_and_rejects_cross_kind_reuse()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var service = Service(store);
        var versionTwoInput = Input(current) with { DisplayName = "Version two" };
        var versionTwo = Assert.IsType<CustomLoopDefinition>((await service.UpdateAsync(current.Id, 1, current.RoleId, "durable-update", "actor-user", versionTwoInput)).Definition);
        var versionThreeInput = Input(versionTwo) with { DisplayName = "Version three" };
        var versionThree = Assert.IsType<CustomLoopDefinition>((await service.UpdateAsync(current.Id, 2, current.RoleId, "later-update", "actor-user", versionThreeInput)).Definition);

        var replay = await service.UpdateAsync(current.Id, 1, current.RoleId, "durable-update", "actor-user", versionTwoInput);
        var changed = await service.UpdateAsync(current.Id, 1, current.RoleId, "durable-update", "actor-user", versionTwoInput with { Description = "Changed request" });
        var crossKind = await service.DeleteAsync(current.Id, versionThree.DefinitionVersion, current.RoleId, "durable-update", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Replayed, replay.Status);
        Assert.Equal(versionTwo.ContentHash, replay.Definition!.ContentHash);
        Assert.Equal(CustomLoopAuthoringStatus.Conflict, changed.Status);
        Assert.Equal(CustomLoopAuthoringStatus.Conflict, crossKind.Status);
        Assert.Equal(2, store.UpdateCallCount);
        Assert.Equal(0, store.DeleteCallCount);
        Assert.Equal(versionThree.ContentHash, (await store.GetAsync(current.Id))!.ContentHash);
    }

    [Fact]
    public async Task Pending_Update_recovery_rechecks_nonterminal_runs_before_mutating()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var input = Input(current) with { DisplayName = "Recovered update" };
        var first = await Service(store).UpdateAsync(current.Id, 1, current.RoleId, "pending-update", "actor-user", input);
        store.RestoreDefinition(current);
        store.MarkOperationPending("pending-update");
        var runStore = new FakeRunStore(current);

        var blocked = await Service(store, runStore: runStore).UpdateAsync(current.Id, 1, current.RoleId, "pending-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, first.Status);
        Assert.Equal(CustomLoopAuthoringStatus.ActiveRunExists, blocked.Status);
        Assert.Same(current, blocked.Definition);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.PendingMutation, (await store.GetMutationOperationAsync("pending-update")).Status);
    }

    [Fact]
    public async Task Pending_Update_recovery_rechecks_the_current_role_tool_ceiling()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var input = Input(current) with { ToolAssignments = [CustomLoopToolAssignment.Read] };
        var first = await Service(store).UpdateAsync(current.Id, 1, current.RoleId, "pending-authority-update", "actor-user", input, [CustomLoopToolAssignment.Read]);
        store.RestoreDefinition(current);
        store.MarkOperationPending("pending-authority-update");

        var blocked = await Service(store).UpdateAsync(current.Id, 1, current.RoleId, "pending-authority-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, first.Status);
        var error = Assert.Single(blocked.ValidationErrors);
        Assert.Equal("tool_assignment_outside_role_ceiling", error.Code);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.PendingMutation, (await store.GetMutationOperationAsync("pending-authority-update")).Status);
    }

    [Fact]
    public async Task Applied_pending_Update_seals_before_active_run_and_role_ceiling_checks()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var input = Input(current) with { ToolAssignments = [CustomLoopToolAssignment.Read] };
        var first = await Service(store).UpdateAsync(current.Id, 1, current.RoleId, "applied-pending-update", "actor-user", input, [CustomLoopToolAssignment.Read]);
        store.MarkOperationPending("applied-pending-update");
        var runStore = new FakeRunStore(current);

        var recovered = await Service(store, runStore: runStore).UpdateAsync(current.Id, 1, current.RoleId, "applied-pending-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, first.Status);
        Assert.Equal(CustomLoopAuthoringStatus.Replayed, recovered.Status);
        Assert.Equal(2, store.UpdateCallCount);
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted, (await store.GetMutationOperationAsync("applied-pending-update")).Status);
    }

    [Fact]
    public async Task Applied_pending_Delete_seals_before_the_active_run_check()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var first = await Service(store).DeleteAsync(current.Id, 1, current.RoleId, "applied-pending-delete", "actor-user");
        store.MarkOperationPending("applied-pending-delete");
        var runStore = new FakeRunStore(current);

        var recovered = await Service(store, runStore: runStore).DeleteAsync(current.Id, 1, current.RoleId, "applied-pending-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Deleted, first.Status);
        Assert.Equal(CustomLoopAuthoringStatus.Replayed, recovered.Status);
        Assert.Equal(2, store.DeleteCallCount);
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted, (await store.GetMutationOperationAsync("applied-pending-delete")).Status);
    }

    [Fact]
    public async Task Mutation_audit_metadata_excludes_definition_prompt_instruction_and_context_content()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);
        var secrets = new[] { "display-secret", "description-secret", "preset-secret", "step-name-secret", "instruction-secret", "decision-secret" };
        var input = Input(current) with
        {
            DisplayName = secrets[0],
            Description = secrets[1],
            TriggerPolicy = current.TriggerPolicy with { PromptSource = CustomLoopTriggerPromptSource.Preset, PresetPrompt = secrets[2] },
            InferenceSteps = [new CustomLoopInferenceStepInput(current.InferenceSteps.Single().Id, secrets[3], secrets[4], CustomLoopNodeContextPolicy.Override(Policy()))],
            ExitPolicy = current.ExitPolicy with { DecisionInstruction = secrets[5], ContextPolicy = CustomLoopNodeContextPolicy.Override(Policy()) }
        };

        var result = await service.UpdateAsync(current.Id, 1, current.RoleId, "safe-audit-update", "actor-user", input);

        Assert.Equal(CustomLoopAuthoringStatus.Updated, result.Status);
        foreach (var auditEvent in audit.Events)
        {
            var auditText = auditEvent.Detail + " " + string.Join(' ', auditEvent.Metadata.Select(item => $"{item.Key}={item.Value}"));
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, auditText, StringComparison.Ordinal));
            Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("context", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Audit_intent_failure_blocks_create_update_and_delete_mutations()
    {
        var createStore = new FakeStore();
        var create = await Service(createStore, RecordingAuditLog.FailingOnAttempt(1)).CreateAsync("role-workspace", "op-create", "actor-user");

        var updateCurrent = Definition();
        var updateStore = new FakeStore(updateCurrent);
        var update = await Service(updateStore, RecordingAuditLog.FailingOnAttempt(1)).UpdateAsync(updateCurrent.Id, updateCurrent.DefinitionVersion, updateCurrent.RoleId, "op-update", "actor-user", Input(updateCurrent));

        var deleteCurrent = Definition();
        var deleteStore = new FakeStore(deleteCurrent);
        var delete = await Service(deleteStore, RecordingAuditLog.FailingOnAttempt(1)).DeleteAsync(deleteCurrent.Id, deleteCurrent.DefinitionVersion, deleteCurrent.RoleId, "op-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.AuditUnavailable, create.Status);
        Assert.Equal(CustomLoopAuthoringStatus.AuditUnavailable, update.Status);
        Assert.Equal(CustomLoopAuthoringStatus.AuditUnavailable, delete.Status);
        Assert.Equal(0, createStore.MutationCallCount);
        Assert.Equal(0, updateStore.MutationCallCount);
        Assert.Equal(0, deleteStore.MutationCallCount);
    }

    [Fact]
    public async Task A_committed_mutation_surfaces_an_outcome_audit_warning_without_hiding_the_definition()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = RecordingAuditLog.FailingOnAttempt(2);
        var service = Service(store, audit);

        var result = await service.UpdateAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-update", "actor-user", Input(current) with { DisplayName = "Committed update" });

        Assert.Equal(CustomLoopAuthoringStatus.CommittedWithAuditWarning, result.Status);
        Assert.True(result.IsCommitted);
        Assert.NotNull(result.Definition);
        Assert.Equal("Committed update", result.Definition.DisplayName);
        Assert.Contains("committed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(2, audit.AppendAttemptCount);
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task Delete_commits_a_tombstone_and_a_retry_replays_without_redeleting()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var deleted = await service.DeleteAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-delete", "actor-user");
        var replayed = await service.DeleteAsync(current.Id, current.DefinitionVersion, current.RoleId, "op-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Deleted, deleted.Status);
        Assert.True(deleted.IsCommitted);
        Assert.Same(current, deleted.Definition);
        Assert.Equal(CustomLoopAuthoringStatus.Replayed, replayed.Status);
        Assert.True(replayed.IsCommitted);
        Assert.Same(current, replayed.Definition);
        Assert.Equal(1, store.DeleteCallCount);
        Assert.NotNull(store.Tombstone);
        Assert.Equal("op-delete", store.Tombstone.MutationOperationId);
        Assert.Equal(Now, store.Tombstone.DeletedAtUtc);
        Assert.Equal(2, audit.Events.Count);
        AssertAudit(audit.Events[0], AuditSchema.Actions.LoopDefinitionMutationIntent, AuditSchema.Outcomes.Requested, "delete", current);
        AssertAudit(audit.Events[1], AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditSchema.Outcomes.Succeeded, "delete", current);
    }

    [Fact]
    public async Task Delete_maps_not_found_and_version_conflict_store_results()
    {
        var deletedTombstone = new CustomLoopDefinitionTombstone(CustomLoopDefinitionTombstone.CurrentSchemaVersion, "loop-missing", 4, new string('a', CustomLoopLimits.Sha256HexCharacters), "other-role-delete", Now.AddMinutes(-5));
        var missingStore = new FakeStore { DeleteResult = CustomLoopDefinitionStoreResult.TombstoneConflict(deletedTombstone, expectedDefinitionVersion: 1) };
        var missing = await Service(missingStore).DeleteAsync("loop-missing", 1, "role-workspace", "op-delete", "actor-user");

        var current = Definition(version: 2);
        var conflictStore = new FakeStore(current)
        {
            DeleteResult = CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion: 1),
            AuditMarkResult = CustomLoopOperationAuditMarkStatus.NotFound
        };
        var conflict = await Service(conflictStore).DeleteAsync(current.Id, 1, current.RoleId, "op-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.NotFound, missing.Status);
        Assert.Equal(0, missingStore.DeleteCallCount);
        Assert.Equal(CustomLoopAuthoringStatus.Conflict, conflict.Status);
        Assert.False(conflict.IsCommitted);
        Assert.Equal(1, conflict.Conflict?.ExpectedDefinitionVersion);
        Assert.Equal(2, conflict.Conflict?.ActualDefinitionVersion);
        Assert.Contains("audit", conflict.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry the same operation id", conflict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_preserves_a_noncommitting_conflict_and_warns_when_the_outcome_audit_fails()
    {
        var current = Definition(version: 2);
        var store = new FakeStore(current) { DeleteResult = CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion: 1) };
        var audit = RecordingAuditLog.FailingOnAttempt(2);

        var result = await Service(store, audit).DeleteAsync(current.Id, 1, current.RoleId, "op-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Conflict, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Contains("outcome audit could not be recorded", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Retry the same operation id", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, store.AuditMarkCallCount);
    }

    [Fact]
    public async Task Delete_rejects_a_role_binding_mismatch_before_run_checks_audit_or_storage()
    {
        var current = Definition();
        var store = new FakeStore(current);
        var audit = new RecordingAuditLog();
        var service = Service(store, audit);

        var result = await service.DeleteAsync(current.Id, current.DefinitionVersion, "role-other", "op-delete", "actor-user");

        Assert.Equal(CustomLoopAuthoringStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "role_binding_mismatch");
        Assert.Equal(0, store.DeleteCallCount);
        Assert.Single(audit.Events);
        Assert.Equal(AuditSchema.Outcomes.Rejected, audit.Events[0].Outcome);
    }

    [Fact]
    public async Task Cancellation_from_the_audit_boundary_is_not_downgraded_to_audit_unavailable()
    {
        var audit = new RecordingAuditLog { CancellationOnAttempt = 1 };
        var service = Service(new FakeStore(), audit);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateAsync("role-workspace", "op-create", "actor-user"));
    }

    [Fact]
    public async Task Unsupported_store_status_is_not_silently_projected_as_success()
    {
        var unknown = new CustomLoopDefinitionStoreResult(CustomLoopDefinitionStoreStatus.Unknown, null, null, null);
        var store = new FakeStore { CreateResult = unknown };
        var service = Service(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("role-workspace", "op-create", "actor-user"));

        Assert.Contains("Unsupported", exception.Message, StringComparison.Ordinal);
    }

    private static CustomLoopAuthoringService Service(FakeStore store, RecordingAuditLog? audit = null, ICustomLoopDefinitionIdentityGenerator? identity = null, ICustomLoopRunStore? runStore = null)
    {
        return new CustomLoopAuthoringService(store, audit ?? new RecordingAuditLog(), identity ?? new QueueIdentityGenerator(["loop-created"], ["step-created", "step-new"]), new FixedTimeProvider(Now), runStore);
    }

    private static CustomLoopDefinition Definition(string operationId = "op-existing", int version = 1)
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-existing", "role-workspace", "step-existing", operationId, Now.AddHours(-1));
        return Rehash(definition with { DefinitionVersion = version, UpdatedAtUtc = Now.AddMinutes(-15), DisplayName = "Existing loop", Description = "Existing description" });
    }

    private static CustomLoopDefinitionInput Input(CustomLoopDefinition? definition = null)
    {
        definition ??= Definition();
        return new CustomLoopDefinitionInput(
            definition.DisplayName,
            definition.Description,
            definition.TriggerPolicy,
            definition.InferenceSteps.Select(step => new CustomLoopInferenceStepInput(step.Id, step.Name, step.Instruction, step.ContextPolicy)).ToArray(),
            definition.ToolAssignments.ToArray(),
            definition.ExitPolicy);
    }

    private static CustomLoopContextPolicy Policy(bool role = true, bool trigger = true, bool conversation = false, bool retained = true, bool previous = true, bool retainOutput = true, bool publish = false)
    {
        return new CustomLoopContextPolicy(
            new CustomLoopContextInputPolicy(role, trigger, conversation, retained, previous),
            new CustomLoopContextOutputPolicy(retainOutput, publish));
    }

    private static CustomLoopDefinition Rehash(CustomLoopDefinition definition)
    {
        return CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = new string('0', CustomLoopLimits.Sha256HexCharacters) });
    }

    private static void AssertAudit(AuditEvent auditEvent, string action, string outcome, string operation, CustomLoopDefinition definition)
    {
        Assert.Equal("actor-user", auditEvent.Actor);
        Assert.Equal(action, auditEvent.Action);
        Assert.Equal(definition.Id, auditEvent.Target);
        Assert.Equal(outcome, auditEvent.Outcome);
        Assert.Equal(operation, auditEvent.Metadata["operation"]);
        Assert.Equal(definition.Id, auditEvent.Metadata["loop_id"]);
        Assert.Equal(definition.DefinitionVersion, auditEvent.Metadata["definition_version"]);
        Assert.Equal(definition.ContentHash, auditEvent.Metadata["content_hash"]);
        Assert.Equal(definition.RoleId, auditEvent.Metadata["role_id"]);
    }

    private static void AssertRejectionAudit(RecordingAuditLog audit, string rejectionCode)
    {
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(AuditSchema.Actions.LoopDefinitionMutationOutcome, auditEvent.Action);
        Assert.Equal(AuditSchema.Outcomes.Rejected, auditEvent.Outcome);
        Assert.Equal(rejectionCode, auditEvent.Metadata["rejection_code"]);
    }

    private sealed class QueueIdentityGenerator : ICustomLoopDefinitionIdentityGenerator
    {
        private readonly Queue<string> _loopIds;
        private readonly Queue<string> _stepIds;

        public QueueIdentityGenerator(IEnumerable<string> loopIds, IEnumerable<string> stepIds)
        {
            _loopIds = new Queue<string>(loopIds);
            _stepIds = new Queue<string>(stepIds);
        }

        public int CallCount { get; private set; }

        public string NewLoopId()
        {
            CallCount++;
            return _loopIds.Dequeue();
        }

        public string NewInferenceStepId()
        {
            CallCount++;
            return _stepIds.Dequeue();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private int? _failureOnAttempt;

        public List<AuditEvent> Events { get; } = [];

        public int AppendAttemptCount { get; private set; }

        public int? CancellationOnAttempt { get; init; }

        public static RecordingAuditLog FailingOnAttempt(int attempt)
        {
            return new RecordingAuditLog { _failureOnAttempt = attempt };
        }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            AppendAttemptCount++;
            if (CancellationOnAttempt == AppendAttemptCount)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (_failureOnAttempt == AppendAttemptCount)
            {
                throw new IOException("Audit unavailable.");
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
        }
    }

    private sealed class FakeRunStore(CustomLoopDefinition definition) : ICustomLoopRunStore
    {
        private readonly CustomLoopRunRecord _run = new(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-active",
            definition.Id,
            1,
            CustomLoopRunStatus.Running,
            Now,
            Now,
            null,
            "test",
            new CustomLoopModelSnapshot("test", null),
            "admit-run",
            "embodysense.test",
            new string('a', CustomLoopLimits.Sha256HexCharacters),
            definition,
            string.Empty,
            null,
            CustomLoopContextSnapshot.CreateEmpty(Now),
            new CustomLoopExecutionClock(0, Now),
            CustomLoopRunCheckpoint.Start(),
            [],
            null,
            null,
            null);

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CustomLoopRunStoreResult.AlreadyCreated(_run));
        }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(string.Equals(runId, _run.Id, StringComparison.Ordinal) ? _run : null);
        }

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(string.Equals(admissionOperationId, _run.AdmissionOperationId, StringComparison.Ordinal) ? _run : null);
        }

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(string.Equals(loopId, _run.LoopId, StringComparison.Ordinal) ? _run : null);
        }

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
        }

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([_run]);
        }

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CustomLoopRunStoreResult.Updated(run));
        }
    }

    private sealed class FakeStore : ICustomLoopDefinitionStore
    {
        private readonly Dictionary<string, CustomLoopDefinition> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CustomLoopDefinitionMutationOperation> _operations = new(StringComparer.Ordinal);

        public FakeStore(params CustomLoopDefinition[] definitions)
        {
            foreach (var definition in definitions)
            {
                _definitions.Add(definition.Id, definition);
            }
        }

        public CustomLoopDefinitionStoreResult? CreateResult { get; init; }

        public CustomLoopDefinitionStoreResult? UpdateResult { get; init; }

        public CustomLoopDefinitionStoreResult? DeleteResult { get; init; }

        public CustomLoopCreateOperationLookupResult? CreateOperationLookupResult { get; init; }

        public CustomLoopOperationAuditMarkStatus AuditMarkResult { get; init; } = CustomLoopOperationAuditMarkStatus.Marked;

        public CustomLoopDefinition? CreatedDefinition { get; private set; }

        public CustomLoopDefinition? UpdatedDefinition { get; private set; }

        public int? UpdatedExpectedVersion { get; private set; }

        public CustomLoopDefinitionTombstone? Tombstone { get; private set; }

        public int ListCallCount { get; private set; }

        public int GetCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        public int UpdateCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public int CreateOperationLookupCallCount { get; private set; }

        public int MutationOperationLookupCallCount { get; private set; }

        public int AuditMarkCallCount { get; private set; }

        public int MutationCallCount => CreateCallCount + UpdateCallCount + DeleteCallCount;

        public void RestoreDefinition(CustomLoopDefinition definition)
        {
            _definitions[definition.Id] = definition;
        }

        public void MarkOperationPending(string operationId)
        {
            _operations[operationId] = _operations[operationId] with
            {
                State = CustomLoopDefinitionMutationState.PendingMutation,
                Outcome = CustomLoopDefinitionStoreStatus.Unknown,
                ResultDefinition = null,
                ResultConflict = null,
                ResultTombstone = null,
                OutcomeAuditRecorded = false
            };
        }

        public Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            CreatedDefinition = definition;
            if (CreateResult is not null)
            {
                return Task.FromResult(CreateResult);
            }

            _definitions.Add(definition.Id, definition);
            return Task.FromResult(CustomLoopDefinitionStoreResult.Created(definition, CustomLoopOperationIntegrity.PendingOutcomeAudit));
        }

        public async Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default)
        {
            var result = await CreateAsync(definition, cancellationToken);
            PersistOperation(mutation, result);
            return _operations[mutation.OperationId].ToStoreResult();
        }

        public Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            CreateOperationLookupCallCount++;
            if (CreateOperationLookupResult is not null)
            {
                return Task.FromResult(CreateOperationLookupResult);
            }

            var definition = _definitions.Values.FirstOrDefault(candidate => string.Equals(candidate.LastMutationOperationId, operationId, StringComparison.Ordinal));
            return Task.FromResult(definition is null
                ? CustomLoopCreateOperationLookupResult.NotFound()
                : CustomLoopCreateOperationLookupResult.Committed(definition, CustomLoopOperationIntegrity.Complete));
        }

        public Task<CustomLoopDefinitionMutationLookupResult> GetMutationOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            MutationOperationLookupCallCount++;
            if (!_operations.TryGetValue(operationId, out var operation))
            {
                return Task.FromResult(CustomLoopDefinitionMutationLookupResult.NotFound());
            }

            return Task.FromResult(CustomLoopDefinitionMutationLookupResult.Found(operation with { HasAppliedMutationArtifact = HasAppliedMutationArtifact(operation) }));
        }

        public Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            _definitions.TryGetValue(loopId, out var definition);
            return Task.FromResult(definition);
        }

        public Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            return Task.FromResult<IReadOnlyList<CustomLoopDefinition>>(_definitions.Values.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToArray());
        }

        public Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            UpdatedDefinition = definition;
            UpdatedExpectedVersion = expectedDefinitionVersion;
            if (UpdateResult is not null)
            {
                return Task.FromResult(UpdateResult);
            }

            _definitions[definition.Id] = definition;
            return Task.FromResult(CustomLoopDefinitionStoreResult.Updated(definition));
        }

        public async Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default)
        {
            var result = await UpdateAsync(definition, expectedDefinitionVersion, cancellationToken);
            PersistOperation(mutation, result);
            return _operations[mutation.OperationId].ToStoreResult();
        }

        public Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            if (DeleteResult is not null)
            {
                return Task.FromResult(DeleteResult);
            }

            if (_definitions.Remove(loopId, out var definition))
            {
                Tombstone = new CustomLoopDefinitionTombstone(CustomLoopDefinitionTombstone.CurrentSchemaVersion, loopId, definition.DefinitionVersion, definition.ContentHash, mutationOperationId, deletedAtUtc);
                return Task.FromResult(CustomLoopDefinitionStoreResult.Deleted(definition, Tombstone));
            }

            if (Tombstone is not null && Tombstone.LastDefinitionVersion == expectedDefinitionVersion && Tombstone.MutationOperationId == mutationOperationId)
            {
                return Task.FromResult(CustomLoopDefinitionStoreResult.AlreadyDeleted(Tombstone));
            }

            return Task.FromResult(CustomLoopDefinitionStoreResult.NotFound());
        }

        public async Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default)
        {
            var result = await DeleteAsync(loopId, expectedDefinitionVersion, mutationOperationId, deletedAtUtc, cancellationToken);
            PersistOperation(mutation, result);
            return _operations[mutation.OperationId].ToStoreResult();
        }

        public Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default)
        {
            AuditMarkCallCount++;
            if (_operations.TryGetValue(operationId, out var operation))
            {
                _operations[operationId] = operation with { OutcomeAuditRecorded = true };
            }

            return Task.FromResult(AuditMarkResult);
        }

        private void PersistOperation(CustomLoopDefinitionMutationRequest mutation, CustomLoopDefinitionStoreResult result)
        {
            _operations[mutation.OperationId] = new CustomLoopDefinitionMutationOperation(
                CustomLoopDefinitionMutationOperation.CurrentSchemaVersion,
                mutation.Kind,
                mutation.OperationId,
                mutation.RequestHash,
                mutation.LoopId,
                mutation.RoleId,
                mutation.ExpectedDefinitionVersion,
                mutation.PlannedDefinition,
                mutation.PriorDefinition,
                mutation.RequestedAtUtc,
                mutation.PlannedDefinition?.UpdatedAtUtc ?? result.Tombstone?.DeletedAtUtc ?? mutation.RequestedAtUtc,
                CustomLoopDefinitionMutationState.OutcomeCommitted,
                result.Status,
                result.Definition,
                result.Conflict,
                result.Tombstone,
                OutcomeAuditRecorded: false);
        }

        private bool HasAppliedMutationArtifact(CustomLoopDefinitionMutationOperation operation)
        {
            return operation.Kind switch
            {
                CustomLoopDefinitionMutationKind.Create or CustomLoopDefinitionMutationKind.Update => operation.PlannedDefinition is not null
                    && _definitions.TryGetValue(operation.LoopId, out var current)
                    && string.Equals(current.ContentHash, operation.PlannedDefinition.ContentHash, StringComparison.Ordinal),
                CustomLoopDefinitionMutationKind.Delete => operation.PriorDefinition is not null
                    && Tombstone is not null
                    && string.Equals(Tombstone.LoopId, operation.LoopId, StringComparison.Ordinal)
                    && string.Equals(Tombstone.LastContentHash, operation.PriorDefinition.ContentHash, StringComparison.Ordinal)
                    && string.Equals(Tombstone.MutationOperationId, operation.OperationId, StringComparison.Ordinal),
                _ => false
            };
        }
    }
}
