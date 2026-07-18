using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopDefinitionStoreTests
{
    private static readonly DateTimeOffset InitialTimestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");

    [Fact]
    public async Task CreateAsync_round_trips_strict_canonical_json_in_the_custom_directory()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");

        var result = await store.CreateAsync(definition);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Created, result.Status);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, result.OperationIntegrity);
        Assert.Same(definition, result.Definition);
        Assert.Null(result.Conflict);
        var path = Path.Combine(paths.CustomLoopDefinitionsPath, "loop-alpha.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(paths.LoopDefinitionsPath, "loop-alpha.json")));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"promptSource\": \"invocation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"inherit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"toolAssignments\": []", json, StringComparison.Ordinal);
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("loop-alpha"));
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
        var loaded = await store.GetAsync("loop-alpha");
        AssertDefinition(definition, loaded);
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopDefinitionsPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UpdateAsync_persists_the_canonical_next_version()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var created = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, created);
        var updated = Advance(created, "Renamed loop", "save-2");

        var result = await store.UpdateAsync(updated, expectedDefinitionVersion: 1);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, result.Status);
        Assert.Same(updated, result.Definition);
        var loaded = await store.GetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.DefinitionVersion);
        Assert.Equal("Renamed loop", loaded.DisplayName);
        Assert.Equal("save-2", loaded.LastMutationOperationId);
        Assert.Equal(CustomLoopDefinitionContentHash.Compute(loaded), loaded.ContentHash);
    }

    [Fact]
    public async Task UpdateAsync_rejects_a_non_successor_canonical_version()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var created = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, created);
        var skipped = CustomLoopDefinitionContentHash.Apply(created with { DefinitionVersion = 3, UpdatedAtUtc = InitialTimestamp.AddMinutes(2), LastMutationOperationId = "save-3" });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(skipped, expectedDefinitionVersion: 1));

        Assert.Contains("exactly one greater", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await store.GetAsync(created.Id))!.DefinitionVersion);
    }

    [Fact]
    public async Task UpdateAsync_rejects_role_changes_before_definition_or_operation_artifacts_are_persisted()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var created = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, created);
        var changedRole = CustomLoopDefinitionContentHash.Apply(Advance(created, "Changed role", "role-change") with { RoleId = "other-role" });
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, changedRole.LastMutationOperationId, 'a', changedRole.Id, changedRole.RoleId, 1, changedRole, created, changedRole.UpdatedAtUtc);

        var directException = await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(changedRole, expectedDefinitionVersion: 1));
        var durableException = await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(changedRole, expectedDefinitionVersion: 1, mutation));

        Assert.Contains("cannot change their directory-role binding", directException.Message, StringComparison.Ordinal);
        Assert.Contains("cannot change their directory-role binding", durableException.Message, StringComparison.Ordinal);
        AssertDefinition(created, await store.GetAsync(created.Id));
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.NotFound, (await store.GetMutationOperationAsync(mutation.OperationId)).Status);
    }

    [Fact]
    public async Task Concurrent_updates_with_the_same_expected_version_allow_exactly_one_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopDefinitionStore(paths);
        var secondStore = new CustomLoopDefinitionStore(paths);
        var created = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(firstStore, created);
        var first = Advance(created, "First writer", "save-first");
        var second = Advance(created, "Second writer", "save-second");

        var results = await Task.WhenAll(
            firstStore.UpdateAsync(first, expectedDefinitionVersion: 1),
            secondStore.UpdateAsync(second, expectedDefinitionVersion: 1));

        Assert.Single(results, result => result.Status == CustomLoopDefinitionStoreStatus.Updated);
        var conflict = Assert.Single(results, result => result.Status == CustomLoopDefinitionStoreStatus.Conflict);
        Assert.NotNull(conflict.Conflict);
        Assert.Equal(1, conflict.Conflict.ExpectedDefinitionVersion);
        Assert.Equal(2, conflict.Conflict.ActualDefinitionVersion);
        var loaded = await firstStore.GetAsync(created.Id);
        Assert.Contains(loaded!.DisplayName, new[] { "First writer", "Second writer" });
    }

    [Fact]
    public async Task CreateAsync_returns_conflict_without_overwriting_an_existing_definition()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var original = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, original);
        var duplicate = CustomLoopDefinitionContentHash.Apply(original with { DisplayName = "Replacement", LastMutationOperationId = "create-duplicate" });

        var result = await store.CreateAsync(duplicate);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, result.Status);
        Assert.Equal(0, result.Conflict!.ExpectedDefinitionVersion);
        Assert.Equal(1, result.Conflict.ActualDefinitionVersion);
        Assert.Equal(original.ContentHash, result.Conflict.CurrentContentHash);
        Assert.Equal(original.DisplayName, (await store.GetAsync(original.Id))!.DisplayName);
    }

    [Fact]
    public async Task Stale_update_and_delete_return_current_safe_conflict_metadata()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var created = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, created);
        var current = Advance(created, "Current", "save-current");
        await store.UpdateAsync(current, expectedDefinitionVersion: 1);
        var staleCandidate = Advance(created, "Stale", "save-stale");

        var update = await store.UpdateAsync(staleCandidate, expectedDefinitionVersion: 1);
        var delete = await store.DeleteAsync(created.Id, expectedDefinitionVersion: 1, "delete-stale", InitialTimestamp.AddMinutes(3));

        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, update.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, delete.Status);
        Assert.All(new[] { update, delete }, result =>
        {
            Assert.Equal(2, result.Conflict!.ActualDefinitionVersion);
            Assert.Equal(current.ContentHash, result.Conflict.CurrentContentHash);
            Assert.Equal(current.UpdatedAtUtc, result.Conflict.CurrentUpdatedAtUtc);
        });
        Assert.Equal("Current", (await store.GetAsync(created.Id))!.DisplayName);
    }

    [Fact]
    public async Task Missing_reads_updates_and_deletes_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var missingUpdate = Advance(CreateDefinition("missing-loop"), "Missing", "save-2");

        var loaded = await store.GetAsync("missing-loop");
        var update = await store.UpdateAsync(missingUpdate, expectedDefinitionVersion: 1);
        var delete = await store.DeleteAsync("missing-loop", expectedDefinitionVersion: 1, "delete-missing", InitialTimestamp.AddMinutes(2));

        Assert.Null(loaded);
        Assert.Equal(CustomLoopDefinitionStoreStatus.NotFound, update.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.NotFound, delete.Status);
    }

    [Fact]
    public async Task ListAsync_returns_only_custom_definitions_in_ordinal_id_order()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(paths.DefaultConversationLoopDefinitionPath, "{}");
        var store = new CustomLoopDefinitionStore(paths);
        await CreateCommittedAsync(store, CreateDefinition("zeta-loop"));
        await CreateCommittedAsync(store, CreateDefinition("alpha-loop"));

        var definitions = await store.ListAsync();

        Assert.Collection(
            definitions,
            definition => Assert.Equal("alpha-loop", definition.Id),
            definition => Assert.Equal("zeta-loop", definition.Id));
    }

    [Fact]
    public async Task ListAsync_returns_empty_when_the_custom_directory_is_missing()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));

        var definitions = await store.ListAsync();

        Assert.Empty(definitions);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPERCASE")]
    [InlineData("has space")]
    [InlineData("-separator-first")]
    [InlineData("separator-last-")]
    [InlineData("loop.")]
    [InlineData("con")]
    [InlineData("com1.txt")]
    [InlineData("nul")]
    public async Task Artifact_operations_reject_unsafe_loop_ids(string loopId)
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var unsafeDefinition = CustomLoopDefinitionContentHash.Apply(CreateDefinition("safe-loop") with { Id = loopId });

        await Assert.ThrowsAsync<FormatException>(() => store.CreateAsync(unsafeDefinition));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(loopId));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(loopId, 1, "delete-safe", InitialTimestamp));
    }

    [Fact]
    public async Task DeleteAsync_rejects_unsafe_operation_ids_and_non_utc_timestamps()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("safe-loop", 1, "../escape", InitialTimestamp));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("safe-loop", 1, "delete-safe", InitialTimestamp.ToOffset(TimeSpan.FromHours(1))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.DeleteAsync("safe-loop", 0, "delete-safe", InitialTimestamp));
    }

    [Fact]
    public async Task CreateAsync_rejects_noncanonical_versions_and_hashes_without_writing_temp_files()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var wrongVersion = CustomLoopDefinitionContentHash.Apply(CreateDefinition("wrong-version") with { DefinitionVersion = 2 });
        var wrongHash = CreateDefinition("wrong-hash") with { ContentHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(wrongVersion));
        var exception = await Assert.ThrowsAsync<FormatException>(() => store.CreateAsync(wrongHash));

        Assert.Contains("Content hash does not match", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.CustomLoopDefinitionsPath));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown-field")]
    [InlineData("unknown-enum")]
    [InlineData("numeric-enum")]
    [InlineData("wrong-property-case")]
    [InlineData("duplicate-field")]
    public async Task GetAsync_rejects_malformed_or_non_strict_json(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        var path = Path.Combine(paths.CustomLoopDefinitionsPath, "loop-alpha.json");
        var json = await File.ReadAllTextAsync(path);
        var corrupted = corruption switch
        {
            "malformed" => "{",
            "unknown-field" => InsertBeforeFinalObjectEnd(json, ",\n  \"additionalFixedContext\": \"forbidden\""),
            "unknown-enum" => json.Replace("\"promptSource\": \"invocation\"", "\"promptSource\": \"telepathy\"", StringComparison.Ordinal),
            "numeric-enum" => json.Replace("\"promptSource\": \"invocation\"", "\"promptSource\": 1", StringComparison.Ordinal),
            "wrong-property-case" => json.Replace("\"displayName\"", "\"DisplayName\"", StringComparison.Ordinal),
            "duplicate-field" => json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        await File.WriteAllTextAsync(path, corrupted);

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));
    }

    [Fact]
    public async Task GetAsync_rejects_an_embedded_id_that_does_not_match_the_filename()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var alpha = CreateDefinition("alpha-loop");
        await CreateCommittedAsync(store, alpha);
        var beta = CreateDefinition("beta-loop");
        await CreateCommittedAsync(store, beta);
        var alphaPath = Path.Combine(paths.CustomLoopDefinitionsPath, "alpha-loop.json");
        var betaPath = Path.Combine(paths.CustomLoopDefinitionsPath, "beta-loop.json");
        await File.WriteAllTextAsync(alphaPath, await File.ReadAllTextAsync(betaPath));

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("alpha-loop"));

        Assert.Contains("does not match its filename", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Precancelled_mutation_leaves_no_artifact_or_temporary_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(CreateDefinition("loop-alpha"), cancellation.Token));

        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionsPath, "loop-alpha.json")));
        Assert.False(Directory.Exists(paths.CustomLoopDefinitionsPath) && Directory.EnumerateFiles(paths.CustomLoopDefinitionsPath, "*.tmp", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task CreateAsync_enforces_the_workspace_definition_limit()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        for (var index = 0; index < CustomLoopLimits.MaxDefinitionsPerWorkspace; index++)
        {
            var definition = CreateDefinition($"loop-{index:D2}");
            var created = await CreateCommittedAsync(store, definition);
            Assert.Equal(CustomLoopDefinitionStoreStatus.Created, created.Status);
        }

        var result = await store.CreateAsync(CreateDefinition("loop-over-limit"));

        Assert.Equal(CustomLoopDefinitionStoreStatus.LimitExceeded, result.Status);
        Assert.Null(await store.GetAsync("loop-over-limit"));
    }

    [Fact]
    public async Task DeleteAsync_writes_a_tombstone_removes_only_the_definition_and_preserves_runs()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, definition.Id);
        Directory.CreateDirectory(runDirectory);
        var runPath = Path.Combine(runDirectory, "run-1.json");
        await File.WriteAllTextAsync(runPath, "historical evidence");
        var deletedAt = InitialTimestamp.AddMinutes(4);

        var result = await store.DeleteAsync(definition.Id, 1, "delete-1", deletedAt);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, result.Status);
        AssertDefinition(definition, result.Definition);
        Assert.NotNull(result.Tombstone);
        Assert.Equal(definition.Id, result.Tombstone.LoopId);
        Assert.Equal(definition.DefinitionVersion, result.Tombstone.LastDefinitionVersion);
        Assert.Equal(definition.ContentHash, result.Tombstone.LastContentHash);
        Assert.Equal("delete-1", result.Tombstone.MutationOperationId);
        Assert.Equal(deletedAt, result.Tombstone.DeletedAtUtc);
        Assert.Null(await store.GetAsync(definition.Id));
        Assert.True(File.Exists(runPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionTombstonesPath, "loop-alpha.json")));
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopDefinitionTombstonesPath, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Repeated_delete_replays_only_the_matching_tombstone_and_other_versions_conflict()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        await store.DeleteAsync(definition.Id, 1, "delete-1", InitialTimestamp.AddMinutes(2));

        var replay = await store.DeleteAsync(definition.Id, 1, "delete-1", InitialTimestamp.AddMinutes(3));
        var otherOperation = await store.DeleteAsync(definition.Id, 1, "delete-2", InitialTimestamp.AddMinutes(3));
        var otherVersion = await store.DeleteAsync(definition.Id, 2, "delete-1", InitialTimestamp.AddMinutes(3));

        Assert.Equal(CustomLoopDefinitionStoreStatus.AlreadyDeleted, replay.Status);
        Assert.Equal("delete-1", replay.Tombstone!.MutationOperationId);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, otherOperation.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, otherVersion.Status);
        Assert.Equal(1, otherVersion.Conflict!.ActualDefinitionVersion);
    }

    [Fact]
    public async Task Tombstoned_ids_cannot_be_recreated_or_updated()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        await store.DeleteAsync(definition.Id, 1, "delete-1", InitialTimestamp.AddMinutes(2));
        var update = Advance(definition, "Attempted resurrection", "save-2");

        var recreate = CustomLoopDefinitionContentHash.Apply(definition with { LastMutationOperationId = "create-again" });
        var recreateResult = await store.CreateAsync(recreate);
        var updateResult = await store.UpdateAsync(update, 1);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, recreateResult.Status);
        Assert.NotNull(recreateResult.Tombstone);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, updateResult.Status);
        Assert.NotNull(updateResult.Tombstone);
        Assert.Null(await store.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Corrupt_tombstones_fail_closed_instead_of_reusing_an_id()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        Directory.CreateDirectory(paths.CustomLoopDefinitionTombstonesPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopDefinitionTombstonesPath, "loop-alpha.json"), "{\"schemaVersion\":1,\"loopId\":\"loop-alpha\",\"unknown\":true}");

        await Assert.ThrowsAsync<FormatException>(() => store.CreateAsync(CreateDefinition("loop-alpha")));
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("loop-alpha"));
        await Assert.ThrowsAsync<FormatException>(() => store.DeleteAsync("loop-alpha", 1, "delete-1", InitialTimestamp));
    }

    [Fact]
    public async Task Create_operation_lookup_and_audit_marker_are_durable_across_store_instances()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");

        var created = await firstStore.CreateAsync(definition);
        var secondStore = new CustomLoopDefinitionStore(paths);
        var pending = await secondStore.GetCreateOperationAsync(definition.LastMutationOperationId);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, created.OperationIntegrity);
        Assert.Equal(CustomLoopCreateOperationLookupStatus.Committed, pending.Status);
        AssertDefinition(definition, pending.Definition);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, pending.OperationIntegrity);
        await Assert.ThrowsAsync<FormatException>(() => secondStore.ListAsync());
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await secondStore.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
        Assert.Equal(CustomLoopOperationAuditMarkStatus.AlreadyMarked, await firstStore.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
        var complete = await new CustomLoopDefinitionStore(paths).GetCreateOperationAsync(definition.LastMutationOperationId);
        Assert.Equal(CustomLoopCreateOperationLookupStatus.Committed, complete.Status);
        Assert.Equal(CustomLoopOperationIntegrity.Complete, complete.OperationIntegrity);
        Assert.Single(await secondStore.ListAsync());
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, definition.LastMutationOperationId + ".json")));
        Assert.Empty(Directory.EnumerateFiles(paths.CustomLoopDefinitionOperationsPath, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Same_Create_operation_and_role_replay_the_original_snapshot_after_update_and_delete()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var original = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, original);
        var updated = Advance(original, "Updated", "save-2");
        await store.UpdateAsync(updated, expectedDefinitionVersion: 1);
        var regeneratedRetry = CustomLoopDefinition.CreateSeed("loop-new-id", original.RoleId, "step-new-id", original.LastMutationOperationId, InitialTimestamp.AddHours(1));

        var replayAfterUpdate = await store.CreateAsync(regeneratedRetry);
        await store.DeleteAsync(original.Id, 2, "delete-2", InitialTimestamp.AddMinutes(3));
        var replayAfterDelete = await store.CreateAsync(regeneratedRetry);

        Assert.Equal(CustomLoopDefinitionStoreStatus.AlreadyCreated, replayAfterUpdate.Status);
        Assert.Equal(CustomLoopOperationIntegrity.Complete, replayAfterUpdate.OperationIntegrity);
        AssertDefinition(original, replayAfterUpdate.Definition);
        Assert.Equal(CustomLoopDefinitionStoreStatus.AlreadyCreated, replayAfterDelete.Status);
        AssertDefinition(original, replayAfterDelete.Definition);
        Assert.Null(await store.GetAsync(original.Id));
        Assert.Null(await store.GetAsync(regeneratedRetry.Id));
    }

    [Fact]
    public async Task Reusing_a_Create_operation_for_another_role_conflicts_without_creating_the_candidate()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var original = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, original);
        var conflictingRequest = CustomLoopDefinition.CreateSeed("loop-other", "role-other", "step-other", original.LastMutationOperationId, InitialTimestamp.AddHours(1));

        var result = await store.CreateAsync(conflictingRequest);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, result.Status);
        Assert.Equal(original.Id, result.Conflict!.LoopId);
        Assert.Null(await store.GetAsync(conflictingRequest.Id));
    }

    [Fact]
    public async Task Pending_Create_without_a_definition_has_a_visible_deterministic_recovery_path()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await firstStore.CreateAsync(definition);
        File.Delete(Path.Combine(paths.CustomLoopDefinitionsPath, definition.Id + ".json"));
        var restartedStore = new CustomLoopDefinitionStore(paths);

        var lookup = await restartedStore.GetCreateOperationAsync(definition.LastMutationOperationId);
        var replay = await restartedStore.CreateAsync(definition);

        Assert.Equal(CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit, lookup.Status);
        AssertDefinition(definition, lookup.Definition);
        Assert.Equal(CustomLoopDefinitionStoreStatus.AlreadyCreated, replay.Status);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, replay.OperationIntegrity);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await restartedStore.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
        AssertDefinition(definition, await restartedStore.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Missing_Create_operation_lookup_and_audit_mark_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));

        var lookup = await store.GetCreateOperationAsync("create-missing");
        var mark = await store.MarkOperationOutcomeAuditedAsync("create-missing");

        Assert.Equal(CustomLoopCreateOperationLookupStatus.NotFound, lookup.Status);
        Assert.Null(lookup.Definition);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.NotFound, mark);
    }

    [Fact]
    public async Task Corrupt_Create_operations_and_missing_operation_lineage_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        var operationPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, definition.LastMutationOperationId + ".json");
        var operationJson = await File.ReadAllTextAsync(operationPath);
        await File.WriteAllTextAsync(operationPath, InsertBeforeFinalObjectEnd(operationJson, ",\n  \"unknown\": true"));

        await Assert.ThrowsAsync<FormatException>(() => store.GetCreateOperationAsync(definition.LastMutationOperationId));
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));

        File.Delete(operationPath);
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));
        await Assert.ThrowsAsync<FormatException>(() => store.ListAsync());
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("tombstone")]
    [InlineData("operation")]
    public async Task Oversized_artifacts_are_rejected_before_deserialization(string artifactKind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var root = artifactKind switch
        {
            "definition" => paths.CustomLoopDefinitionsPath,
            "tombstone" => paths.CustomLoopDefinitionTombstonesPath,
            "operation" => paths.CustomLoopDefinitionOperationsPath,
            _ => throw new InvalidOperationException()
        };
        Directory.CreateDirectory(root);
        var fileName = artifactKind == "operation" ? "create-alpha.json" : "loop-alpha.json";
        var size = artifactKind == "tombstone" ? 20 * 1024 : 700 * 1024;
        await File.WriteAllTextAsync(Path.Combine(root, fileName), new string('x', size));

        var exception = artifactKind == "operation"
            ? await Assert.ThrowsAsync<FormatException>(() => store.GetCreateOperationAsync("create-alpha"))
            : await Assert.ThrowsAsync<FormatException>(() => store.ListAsync());

        Assert.Contains("exceeds the maximum artifact size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_and_mutations_fail_closed_when_another_process_holds_the_workspace_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        var lockPath = Path.Combine(paths.LoopDefinitionsPath, ".custom-loop-mutations.lock");
        await using var externalLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new CustomLoopDefinitionStore(paths);

        var getException = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("loop-alpha"));
        var listException = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync());
        var createException = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(CreateDefinition("loop-alpha")));

        Assert.All(new[] { getException, listException, createException }, exception => Assert.Contains("locked by another process", exception.Message, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionsPath, "loop-alpha.json")));
    }

    [Fact]
    public async Task Direct_Create_mutation_rejects_a_noncanonical_request_hash_without_persisting_artifacts()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var definition = CreateDefinition("loop-alpha");
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Create, definition.LastMutationOperationId, 'f', definition.Id, definition.RoleId, null, definition, null, definition.CreatedAtUtc);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(definition, mutation));

        Assert.Contains("canonical role-bound request", exception.Message, StringComparison.Ordinal);
        Assert.Null(await store.GetAsync(definition.Id));
        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.NotFound, (await store.GetMutationOperationAsync(mutation.OperationId)).Status);
    }

    [Fact]
    public async Task Reparse_point_artifact_roots_are_rejected_when_the_platform_allows_links()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        var target = workspace.File("reparse-target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(paths.CustomLoopDefinitionsPath, target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var store = new CustomLoopDefinitionStore(paths);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync());
        Assert.Contains("reparse points or junctions", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("request-hash")]
    [InlineData("metadata")]
    [InlineData("timestamp")]
    [InlineData("kind")]
    [InlineData("state")]
    [InlineData("outcome")]
    public async Task Invalid_Create_operation_metadata_fails_closed(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        var path = Path.Combine(paths.CustomLoopDefinitionOperationsPath, definition.LastMutationOperationId + ".json");
        var operation = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        switch (corruption)
        {
            case "schema":
                operation["schemaVersion"] = 99;
                break;
            case "request-hash":
                operation["requestHash"] = "bad";
                break;
            case "metadata":
                operation["roleId"] = "role-other";
                break;
            case "timestamp":
                operation["recordedAtUtc"] = InitialTimestamp.AddMinutes(1).ToString("O");
                break;
            case "kind":
                operation["kind"] = "delete";
                break;
            case "state":
                operation["state"] = "unknown";
                break;
            case "outcome":
                operation["outcome"] = "updated";
                break;
        }

        await File.WriteAllTextAsync(path, operation.ToJsonString());

        await Assert.ThrowsAsync<FormatException>(() => store.GetCreateOperationAsync(definition.LastMutationOperationId));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("hash")]
    [InlineData("timestamp")]
    public async Task Invalid_tombstone_metadata_fails_closed(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        await store.DeleteAsync(definition.Id, 1, "delete-1", InitialTimestamp.AddMinutes(2));
        var path = Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json");
        var tombstone = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        switch (corruption)
        {
            case "schema":
                tombstone["schemaVersion"] = 99;
                break;
            case "hash":
                tombstone["lastContentHash"] = "bad";
                break;
            case "timestamp":
                tombstone["deletedAtUtc"] = InitialTimestamp.ToOffset(TimeSpan.FromHours(1)).ToString("O");
                break;
        }

        await File.WriteAllTextAsync(path, tombstone.ToJsonString());

        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Completed_Create_operations_without_an_artifact_and_dual_live_deleted_state_fail_closed()
    {
        using var firstWorkspace = new TestWorkspace();
        var firstPaths = new WorkspacePaths(firstWorkspace.RootPath);
        var firstStore = new CustomLoopDefinitionStore(firstPaths);
        var first = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(firstStore, first);
        File.Delete(Path.Combine(firstPaths.CustomLoopDefinitionsPath, first.Id + ".json"));
        await Assert.ThrowsAsync<FormatException>(() => firstStore.GetCreateOperationAsync(first.LastMutationOperationId));

        using var secondWorkspace = new TestWorkspace();
        var secondPaths = new WorkspacePaths(secondWorkspace.RootPath);
        var secondStore = new CustomLoopDefinitionStore(secondPaths);
        var second = CreateDefinition("loop-beta");
        await CreateCommittedAsync(secondStore, second);
        var definitionPath = Path.Combine(secondPaths.CustomLoopDefinitionsPath, second.Id + ".json");
        var definitionJson = await File.ReadAllTextAsync(definitionPath);
        await secondStore.DeleteAsync(second.Id, 1, "delete-beta", InitialTimestamp.AddMinutes(2));
        await File.WriteAllTextAsync(definitionPath, definitionJson);
        await Assert.ThrowsAsync<FormatException>(() => secondStore.GetAsync(second.Id));
    }

    [Fact]
    public async Task Valid_tombstones_without_their_Create_operation_are_visible_as_degraded()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        await store.DeleteAsync(definition.Id, 1, "delete-1", InitialTimestamp.AddMinutes(2));
        File.Delete(Path.Combine(paths.CustomLoopDefinitionOperationsPath, definition.LastMutationOperationId + ".json"));

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));

        Assert.Contains("missing its durable Create operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durable_mutation_operations_replay_original_results_and_reject_global_reuse_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var original = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, original);
        var versionTwo = Advance(original, "Version two", "update-one");
        var updateOne = Mutation(CustomLoopDefinitionMutationKind.Update, versionTwo.LastMutationOperationId, 'a', versionTwo.Id, versionTwo.RoleId, 1, versionTwo, original, versionTwo.UpdatedAtUtc);
        var first = await store.UpdateAsync(versionTwo, 1, updateOne);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(updateOne.OperationId));

        var versionThree = Advance(versionTwo, "Version three", "update-two");
        var updateTwo = Mutation(CustomLoopDefinitionMutationKind.Update, versionThree.LastMutationOperationId, 'b', versionThree.Id, versionThree.RoleId, 2, versionThree, versionTwo, versionThree.UpdatedAtUtc);
        await store.UpdateAsync(versionThree, 2, updateTwo);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(updateTwo.OperationId));

        var restarted = new CustomLoopDefinitionStore(paths);
        var replay = await restarted.UpdateAsync(versionTwo, 1, updateOne);
        var changedRequest = await restarted.UpdateAsync(versionTwo, 1, updateOne with { RequestHash = new string('c', CustomLoopLimits.Sha256HexCharacters) });
        var crossKind = Mutation(CustomLoopDefinitionMutationKind.Delete, updateOne.OperationId, 'd', versionThree.Id, versionThree.RoleId, 3, null, versionThree, versionThree.UpdatedAtUtc.AddMinutes(1));
        var crossKindResult = await restarted.DeleteAsync(versionThree.Id, 3, crossKind.OperationId, crossKind.RequestedAtUtc, crossKind);

        var other = CreateDefinition("loop-beta") with { CreatedAtUtc = InitialTimestamp.AddHours(1), UpdatedAtUtc = InitialTimestamp.AddHours(1) };
        other = CustomLoopDefinitionContentHash.Apply(other);
        await CreateCommittedAsync(restarted, other);
        var otherUpdate = Advance(other, "Other", updateOne.OperationId);
        var crossLoop = Mutation(CustomLoopDefinitionMutationKind.Update, updateOne.OperationId, 'e', other.Id, other.RoleId, 1, otherUpdate, other, otherUpdate.UpdatedAtUtc);
        var crossLoopResult = await restarted.UpdateAsync(otherUpdate, 1, crossLoop);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, first.Status);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, first.OperationIntegrity);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, replay.Status);
        Assert.Equal(CustomLoopOperationIntegrity.Complete, replay.OperationIntegrity);
        AssertDefinition(versionTwo, replay.Definition);
        AssertDefinition(versionThree, await restarted.GetAsync(versionThree.Id));
        Assert.Equal(CustomLoopDefinitionStoreStatus.OperationConflict, changedRequest.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.OperationConflict, crossKindResult.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.OperationConflict, crossLoopResult.Status);
        AssertDefinition(other, await restarted.GetAsync(other.Id));
    }

    [Fact]
    public async Task Durable_Delete_replays_its_original_definition_snapshot_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(store, definition);
        var deletedAt = InitialTimestamp.AddMinutes(2);
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-durable", 'd', definition.Id, definition.RoleId, 1, null, definition, deletedAt);

        var deleted = await store.DeleteAsync(definition.Id, 1, mutation.OperationId, deletedAt, mutation);
        await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId);
        var restarted = new CustomLoopDefinitionStore(paths);
        var replay = await restarted.DeleteAsync(definition.Id, 1, mutation.OperationId, deletedAt, mutation);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, deleted.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, replay.Status);
        Assert.Equal(CustomLoopOperationIntegrity.Complete, replay.OperationIntegrity);
        AssertDefinition(definition, replay.Definition);
        Assert.Null(await restarted.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Pending_Delete_recovers_after_tombstone_write_before_live_definition_removal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths);
        var definition = CreateDefinition("loop-interrupted-delete");
        await CreateCommittedAsync(store, definition);
        var definitionPath = Path.Combine(paths.CustomLoopDefinitionsPath, definition.Id + ".json");
        var definitionJson = await File.ReadAllTextAsync(definitionPath);
        var deletedAt = InitialTimestamp.AddMinutes(2);
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-interrupted", 'd', definition.Id, definition.RoleId, 1, null, definition, deletedAt);
        await store.DeleteAsync(definition.Id, 1, mutation.OperationId, deletedAt, mutation);
        await RewriteOperationAsPendingAsync(paths, mutation.OperationId);
        await File.WriteAllTextAsync(definitionPath, definitionJson);

        var restarted = new CustomLoopDefinitionStore(paths);
        var pending = await restarted.GetMutationOperationAsync(mutation.OperationId);
        var recovered = await restarted.DeleteAsync(definition.Id, 1, mutation.OperationId, deletedAt, mutation);
        await restarted.MarkOperationOutcomeAuditedAsync(mutation.OperationId);

        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.PendingMutation, pending.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, recovered.Status);
        AssertDefinition(definition, recovered.Definition);
        Assert.Null(await restarted.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Pending_Update_and_Delete_receipts_recover_deterministically_after_a_simulated_restart()
    {
        using var updateWorkspace = new TestWorkspace();
        var updatePaths = new WorkspacePaths(updateWorkspace.RootPath);
        var updateStore = new CustomLoopDefinitionStore(updatePaths);
        var original = CreateDefinition("loop-alpha");
        await CreateCommittedAsync(updateStore, original);
        var originalPath = Path.Combine(updatePaths.CustomLoopDefinitionsPath, original.Id + ".json");
        var originalJson = await File.ReadAllTextAsync(originalPath);
        var updated = Advance(original, "Recovered update", "update-recovery");
        var updateMutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, 'a', updated.Id, updated.RoleId, 1, updated, original, updated.UpdatedAtUtc);
        await updateStore.UpdateAsync(updated, 1, updateMutation);
        await RewriteOperationAsPendingAsync(updatePaths, updateMutation.OperationId);
        await File.WriteAllTextAsync(originalPath, originalJson);

        var restartedUpdateStore = new CustomLoopDefinitionStore(updatePaths);
        var pendingUpdate = await restartedUpdateStore.GetMutationOperationAsync(updateMutation.OperationId);
        var recoveredUpdate = await restartedUpdateStore.UpdateAsync(updated, 1, updateMutation);
        await restartedUpdateStore.MarkOperationOutcomeAuditedAsync(updateMutation.OperationId);

        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.PendingMutation, pendingUpdate.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, recoveredUpdate.Status);
        AssertDefinition(updated, await restartedUpdateStore.GetAsync(updated.Id));

        using var deleteWorkspace = new TestWorkspace();
        var deletePaths = new WorkspacePaths(deleteWorkspace.RootPath);
        var deleteStore = new CustomLoopDefinitionStore(deletePaths);
        var deleteDefinition = CreateDefinition("loop-delete");
        await CreateCommittedAsync(deleteStore, deleteDefinition);
        var deleteDefinitionPath = Path.Combine(deletePaths.CustomLoopDefinitionsPath, deleteDefinition.Id + ".json");
        var deleteJson = await File.ReadAllTextAsync(deleteDefinitionPath);
        var deletedAt = InitialTimestamp.AddMinutes(2);
        var deleteMutation = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-recovery", 'd', deleteDefinition.Id, deleteDefinition.RoleId, 1, null, deleteDefinition, deletedAt);
        await deleteStore.DeleteAsync(deleteDefinition.Id, 1, deleteMutation.OperationId, deletedAt, deleteMutation);
        await RewriteOperationAsPendingAsync(deletePaths, deleteMutation.OperationId);
        File.Delete(Path.Combine(deletePaths.CustomLoopDefinitionTombstonesPath, deleteDefinition.Id + ".json"));
        await File.WriteAllTextAsync(deleteDefinitionPath, deleteJson);

        var restartedDeleteStore = new CustomLoopDefinitionStore(deletePaths);
        var pendingDelete = await restartedDeleteStore.GetMutationOperationAsync(deleteMutation.OperationId);
        var recoveredDelete = await restartedDeleteStore.DeleteAsync(deleteDefinition.Id, 1, deleteMutation.OperationId, deletedAt, deleteMutation);
        await restartedDeleteStore.MarkOperationOutcomeAuditedAsync(deleteMutation.OperationId);

        Assert.Equal(CustomLoopDefinitionMutationLookupStatus.PendingMutation, pendingDelete.Status);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, recoveredDelete.Status);
        AssertDefinition(deleteDefinition, recoveredDelete.Definition);
        Assert.Null(await restartedDeleteStore.GetAsync(deleteDefinition.Id));
    }

    private static CustomLoopDefinition CreateDefinition(string id)
    {
        return CustomLoopDefinition.CreateSeed(id, "default-assistant", $"{id}-step-1", $"create-{id}", InitialTimestamp);
    }

    private static CustomLoopDefinitionMutationRequest Mutation(CustomLoopDefinitionMutationKind kind, string operationId, char hashCharacter, string loopId, string roleId, int? expectedVersion, CustomLoopDefinition? planned, CustomLoopDefinition? prior, DateTimeOffset requestedAt)
    {
        return new CustomLoopDefinitionMutationRequest(kind, operationId, new string(hashCharacter, CustomLoopLimits.Sha256HexCharacters), loopId, roleId, expectedVersion, planned, prior, requestedAt);
    }

    private static async Task RewriteOperationAsPendingAsync(WorkspacePaths paths, string operationId)
    {
        var path = Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json");
        var operation = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        operation["state"] = "pendingMutation";
        operation["outcome"] = "unknown";
        operation["resultDefinition"] = null;
        operation["resultConflict"] = null;
        operation["resultTombstone"] = null;
        operation["outcomeAuditRecorded"] = false;
        operation["updatedAtUtc"] = operation["requestedAtUtc"]!.DeepClone();
        await File.WriteAllTextAsync(path, operation.ToJsonString());
    }

    private static async Task<CustomLoopDefinitionStoreResult> CreateCommittedAsync(CustomLoopDefinitionStore store, CustomLoopDefinition definition)
    {
        var result = await store.CreateAsync(definition);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Created, result.Status);
        Assert.Equal(CustomLoopOperationIntegrity.PendingOutcomeAudit, result.OperationIntegrity);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
        return result;
    }

    private static CustomLoopDefinition Advance(CustomLoopDefinition definition, string displayName, string mutationOperationId)
    {
        return CustomLoopDefinitionContentHash.Apply(definition with
        {
            DefinitionVersion = definition.DefinitionVersion + 1,
            UpdatedAtUtc = definition.UpdatedAtUtc.AddMinutes(1),
            DisplayName = displayName,
            LastMutationOperationId = mutationOperationId
        });
    }

    private static string InsertBeforeFinalObjectEnd(string json, string value)
    {
        var finalObjectEnd = json.LastIndexOf('}');
        return json.Insert(finalObjectEnd, value);
    }

    private static void AssertDefinition(CustomLoopDefinition expected, CustomLoopDefinition? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DefinitionVersion, actual.DefinitionVersion);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.RoleId, actual.RoleId);
        Assert.Equal(expected.TriggerPolicy, actual.TriggerPolicy);
        Assert.Equal(expected.ContextDefaults, actual.ContextDefaults);
        Assert.Equal(expected.InferenceSteps, actual.InferenceSteps);
        Assert.Equal(expected.ToolAssignments, actual.ToolAssignments);
        Assert.Equal(expected.ExitPolicy, actual.ExitPolicy);
        Assert.Equal(expected.LastMutationOperationId, actual.LastMutationOperationId);
    }
}
