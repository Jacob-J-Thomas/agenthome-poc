using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Workspace.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class PersistencePublicBoundaryCoverageTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Workspace_scaffolder_creates_directories_seeds_files_preserves_user_content_and_audits()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preserved = Path.Combine(workspace.RootPath, "preserved", "user.txt");
        var overwritten = Path.Combine(workspace.RootPath, "generated", "readme.txt");
        var fresh = Path.Combine(workspace.RootPath, "nested", "fresh.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
        Directory.CreateDirectory(Path.GetDirectoryName(overwritten)!);
        await File.WriteAllTextAsync(preserved, "user-content");
        await File.WriteAllTextAsync(overwritten, "stale-generated-content");
        var directories = new[] { paths.AgentPath, paths.AuditPath, paths.WorkspaceGeneratedPath, paths.WorkspaceSharedPath };
        var seeds = new[]
        {
            new WorkspaceSeedFile(preserved, "must-not-overwrite", false),
            new WorkspaceSeedFile(overwritten, "current-generated-content", true),
            new WorkspaceSeedFile(fresh, "fresh-content", false)
        };

        await new WorkspaceScaffolder().ApplyAsync(paths, directories, seeds, AuditSchema.Actors.Cli);

        Assert.All(directories, directory => Assert.True(Directory.Exists(directory)));
        Assert.Equal("user-content", await File.ReadAllTextAsync(preserved));
        Assert.Equal("current-generated-content", await File.ReadAllTextAsync(overwritten));
        Assert.Equal("fresh-content", await File.ReadAllTextAsync(fresh));
        var audit = Assert.Single(await new AuditLog(paths).ReadTailAsync(10));
        Assert.Equal(AuditSchema.Actions.WorkspaceInit, audit.Action);
        Assert.Equal(AuditSchema.Actors.Cli, audit.Actor);
        Assert.Equal(paths.AgentPath, audit.Metadata["agent_path"]?.ToString());
        Assert.Equal(paths.AuditPath, audit.Metadata["audit_path"]?.ToString());
        Assert.Equal(paths.PermissionsPath, audit.Metadata["permissions_path"]?.ToString());
        Assert.Equal(paths.WorkspacePath, audit.Metadata["workspace_path"]?.ToString());
    }

    [Fact]
    public async Task Workspace_scaffolder_rejects_null_public_inputs()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var scaffolder = new WorkspaceScaffolder();

        await Assert.ThrowsAsync<ArgumentNullException>(() => scaffolder.ApplyAsync(null!, [], []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => scaffolder.ApplyAsync(paths, null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => scaffolder.ApplyAsync(paths, [], null!));
    }

    [Fact]
    public void Permission_policy_store_loads_defaults_valid_documents_and_malformed_fallbacks()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new PermissionPolicyStore();

        Assert.False(store.Load(paths).HasDocument);
        var defaultJson = store.CreateDefaultJson(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PermissionsPath)!);
        File.WriteAllText(paths.PermissionsPath, defaultJson);
        var loaded = store.Load(paths);
        Assert.True(loaded.HasDocument);
        Assert.NotEmpty(loaded.Approved);

        File.WriteAllText(paths.PermissionsPath, "{ malformed");
        Assert.False(store.Load(paths).HasDocument);
        Assert.Throws<ArgumentNullException>(() => store.Load(null!));
        Assert.Throws<ArgumentNullException>(() => store.CreateDefaultJson(null!));
    }

    [Fact]
    public async Task Control_operation_store_covers_not_found_replay_conflict_and_completion_boundaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths);
        var pending = PendingControl("control-boundary", "web");
        var completed = CompleteControl(pending);

        Assert.Null(await store.GetAsync(pending.OperationId));
        Assert.Equal(CustomLoopControlOperationStoreStatus.NotFound, (await store.CompleteAsync(completed)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(pending));
        var created = await store.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        completed = CompleteControl(created.Operation!);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(completed)).Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Replayed, (await store.CompleteAsync(completed)).Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Conflict, (await store.CompleteAsync(completed with { Detail = "Different completed detail." })).Status);

        var differentActor = CompleteControl(PendingControl(pending.OperationId, "cli"));
        Assert.Equal(CustomLoopControlOperationStoreStatus.Conflict, (await store.CompleteAsync(differentActor)).Status);
        Assert.Throws<ArgumentNullException>(() => new CustomLoopControlOperationStore(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("../unsafe"));
    }

    [Fact]
    public async Task Control_operation_store_rejects_invalid_pending_and_completed_receipts()
    {
        using var workspace = new TestWorkspace();
        var store = new CustomLoopControlOperationStore(new WorkspacePaths(workspace.RootPath));
        var pending = PendingControl("control-invalid", "web");

        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(null!));
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending with { SchemaVersion = 99 }));
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending with { RequestHash = new string('0', 64) }));
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending with { Outcome = CustomLoopControlStatus.Paused }));
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending with { ResultLifecycleVersion = 2 }));
        await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(pending with { State = CustomLoopControlOperationState.Complete }));
        await Assert.ThrowsAsync<FormatException>(() => store.CompleteAsync(pending with { State = CustomLoopControlOperationState.Complete, Outcome = CustomLoopControlStatus.Paused }));
    }

    [Fact]
    public async Task Control_operation_store_rejects_corrupt_json_and_filename_identity_mismatch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopControlOperationStore(paths);
        Directory.CreateDirectory(paths.CustomLoopControlOperationsPath);
        var corruptPath = Path.Combine(paths.CustomLoopControlOperationsPath, "control-corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{ malformed");
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("control-corrupt"));

        var pending = PendingControl("control-embedded", "web");
        await Assert.ThrowsAsync<FormatException>(() => store.BeginAsync(pending));
        File.Delete(corruptPath);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var source = Path.Combine(paths.CustomLoopControlOperationsPath, pending.OperationId + ".json");
        var mismatch = Path.Combine(paths.CustomLoopControlOperationsPath, "control-other.json");
        File.Copy(source, mismatch);
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("control-other"));
    }

    [Fact]
    public void Artifact_deserializer_rejects_invalid_json_utf8_duplicate_fields_and_unsupported_headers()
    {
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes("{")));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize([0xff]));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes("[]")));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes("\"text\"")));

        var artifact = Artifact();
        var json = Encoding.UTF8.GetString(artifact);
        var duplicate = json.Replace("\"artifactKind\":", "\"artifactKind\":\"custom-loop-run\",\"artifactKind\":", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(duplicate)));

        Reject(root => root["artifactKind"] = "other-kind");
        Reject(root => root["artifactSchemaVersion"] = 2);
        Reject(root => root["projectionSchemaVersion"] = 2);
        Reject(root => root["encoding"] = "utf-16");
        Reject(root => root["unexpected"] = true);
    }

    [Fact]
    public void Artifact_deserializer_rejects_malformed_required_root_values()
    {
        Reject(root => root["artifactKind"] = 1);
        Reject(root => root["artifactSchemaVersion"] = "one");
        Reject(root => root["projectionSchemaVersion"] = true);
        Reject(root => root["content"] = new JsonObject());
        Reject(root => root["contextBlocks"] = "blocks");
        Reject(root => root["authorities"] = false);
        Reject(root => root["toolRequests"] = 1);
        Reject(root => root["run"] = new JsonArray());
    }

    [Fact]
    public void Artifact_deserializer_rejects_malformed_content_table_entries()
    {
        Reject(root => Content(root)[0] = true);
        Reject(root => Entry(root)["base64"] = "not-base64!");
        Reject(root => Entry(root)["base64"] = Entry(root)["base64"]!.GetValue<string>() + "\r\n");
        Reject(root =>
        {
            var bytes = new byte[] { 0xff };
            var entry = Entry(root);
            entry["base64"] = Convert.ToBase64String(bytes);
            entry["utf8Bytes"] = bytes.Length;
        });
        Reject(root => Entry(root)["utf16Characters"] = Entry(root)["utf16Characters"]!.GetValue<int>() + 1);
        Reject(root => Entry(root)["sha256"] = new string('0', 64));
        Reject(root => Entry(root)["id"] = "czzz");
    }

    [Fact]
    public void Artifact_deserializer_rejects_duplicate_unreferenced_and_dangling_content()
    {
        Reject(root =>
        {
            var content = Content(root);
            var duplicate = Entry(root).DeepClone().AsObject();
            duplicate["id"] = IndexedId("c", content.Count);
            content.Add(duplicate);
        });
        Reject(root =>
        {
            var content = Content(root);
            content.Add(ContentEntry(IndexedId("c", content.Count), "unreferenced-content"));
        });
        Reject(root => root["run"]!["triggerPrompt"]!["$content"] = "czzz");
    }

    [Fact]
    public void Artifact_deserializer_rejects_malformed_duplicate_unreferenced_and_dangling_structures()
    {
        Reject(root => Blocks(root).Add(true));
        Reject(root => Blocks(root).Add(StructuralEntry("b0", "contextBlock", new JsonObject(), new string('0', 64))));
        Reject(root => Blocks(root).Add(StructuralEntry("b0", "contextBlock", new JsonObject())));
        Reject(root =>
        {
            var blocks = Blocks(root);
            blocks.Add(StructuralEntry("b0", "contextBlock", new JsonObject()));
            blocks.Add(StructuralEntry("b1", "contextBlock", new JsonObject()));
        });
        Reject(root =>
        {
            Blocks(root).Add(StructuralEntry("b0", "contextBlock", new JsonObject()));
            FirstEvent(root)["contextBlocks"] = new JsonArray(new JsonObject { ["$contextBlock"] = "b1" });
        });
        Reject(root =>
        {
            Blocks(root).Add(StructuralEntry("b0", "contextBlock", new JsonObject()));
            FirstEvent(root)["contextBlocks"] = new JsonArray(new JsonObject { ["$contextBlock"] = "b0", ["extra"] = true });
        });
    }

    [Fact]
    public void Artifact_deserializer_rejects_bare_and_array_content_references_in_structural_tables()
    {
        Reject(root =>
        {
            var value = new JsonObject { ["$content"] = "c0" };
            Authorities(root).Add(StructuralEntry("a0", "authority", value));
        });
        Reject(root =>
        {
            var value = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["$content"] = "czzz" }) };
            Authorities(root).Add(StructuralEntry("a0", "authority", value));
        });
    }

    [Fact]
    public void Artifact_deserializer_rejects_unknown_or_semantically_invalid_hydrated_runs()
    {
        Reject(root => root["run"]!["unknownField"] = true);
        var unsupportedSchema = Parse(Artifact());
        unsupportedSchema["run"]!["schemaVersion"] = 99;
        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(unsupportedSchema.ToJsonString())));
        Assert.Contains("Pre-1.0 artifacts from another schema are unsupported", exception.Message, StringComparison.Ordinal);
        Reject(root => root["run"]!["events"]![0] = true);
        Reject(root => FirstEvent(root)["contextBlocks"] = new JsonArray(true));
    }

    [Fact]
    public void Artifact_deserializer_reports_canonical_run_depth_as_artifact_nesting()
    {
        var nested = NestedJson(65);

        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(nested)));

        Assert.Contains("maximum persisted JSON nesting depth of 64", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a loop-iteration, traversal, or run-duration limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_deserializer_requires_the_single_canonical_byte_encoding()
    {
        var artifact = Artifact();
        var trailingWhitespace = artifact.Concat(Encoding.UTF8.GetBytes(" \r\n")).ToArray();
        var trailing = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(trailingWhitespace));
        Assert.Contains("first differing byte", trailing.Message, StringComparison.Ordinal);

        var pretty = Encoding.UTF8.GetBytes(Parse(artifact).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var reformatted = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(pretty));
        Assert.Contains("first differing byte", reformatted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_deserializer_rejects_compact_json_with_noncanonical_projected_property_order()
    {
        var root = Parse(Artifact());
        var run = root["run"]!.AsObject();
        var properties = run.ToArray();
        var reordered = new JsonObject
        {
            [properties[1].Key] = properties[1].Value?.DeepClone(),
            [properties[0].Key] = properties[0].Value?.DeepClone()
        };
        foreach (var property in properties.Skip(2))
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        root["run"] = reordered;
        var noncanonical = Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");

        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(noncanonical));

        Assert.Contains("not in canonical serializer order", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_deserializer_rejects_omitted_default_fields_and_alternate_typed_primitive_spellings()
    {
        var omitted = Parse(Artifact());
        omitted["run"]!.AsObject().Remove("failureDetail");
        var omission = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(omitted.ToJsonString() + "\n")));
        Assert.Contains("not in canonical serializer order", omission.Message, StringComparison.Ordinal);

        var alternate = Parse(Artifact());
        var run = alternate["run"]!.AsObject();
        var canonicalTimestamp = run["createdAtUtc"]!.GetValue<string>();
        run["createdAtUtc"] = canonicalTimestamp.EndsWith('Z') ? canonicalTimestamp[..^1] + "+00:00" : canonicalTimestamp.Replace("+00:00", "Z", StringComparison.Ordinal);
        var spelling = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(alternate.ToJsonString() + "\n")));
        Assert.Contains("does not use its canonical serializer spelling", spelling.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_deserializer_rejects_noncanonical_integer_spellings_in_untyped_content_metadata()
    {
        var canonical = Encoding.UTF8.GetString(Artifact());
        foreach (var propertyName in new[] { "utf16Characters", "utf8Bytes" })
        {
            var noncanonical = canonical.Replace($"\"{propertyName}\":0", $"\"{propertyName}\":-0", StringComparison.Ordinal);
            Assert.NotEqual(canonical, noncanonical);

            var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(noncanonical)));

            Assert.Contains("does not use its canonical serializer spelling", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Artifact_deserializer_rejects_alternate_string_escape_spellings_in_headers_and_projected_fields()
    {
        var canonical = Encoding.UTF8.GetString(Artifact());
        var escapedHeader = canonical.Replace("\"artifactKind\":\"custom-loop-run\"", "\"artifactKind\":\"custom-loop-\\u0072un\"", StringComparison.Ordinal);
        Assert.NotEqual(canonical, escapedHeader);
        _ = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(escapedHeader)));

        var escapedProjection = canonical.Replace("\"id\":\"run-boundary\"", "\"id\":\"run-bounda\\u0072y\"", StringComparison.Ordinal);
        Assert.NotEqual(canonical, escapedProjection);
        _ = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(escapedProjection)));
    }

    [Fact]
    public void Artifact_deserializer_rejects_rehashed_structural_payloads_with_noncanonical_typed_property_order()
    {
        var contextRoot = Parse(Artifact());
        var contextBlock = new JsonObject
        {
            ["source"] = "harnessGovernance",
            ["sourceId"] = new JsonObject { ["$content"] = "c0" },
            ["role"] = "system",
            ["included"] = true,
            ["omissionReason"] = null,
            ["content"] = new JsonObject { ["$content"] = "c0" },
            ["contentHash"] = new string('0', 64),
            ["characterCount"] = 0,
            ["truncated"] = false,
            ["sourceVersion"] = null
        };
        Blocks(contextRoot).Add(StructuralEntry("b0", "contextBlock", ReverseProperties(contextBlock)));
        FirstEvent(contextRoot)["contextBlocks"]!.AsArray().Add(new JsonObject { ["$contextBlock"] = "b0" });
        AssertCanonicalTypedOrderRejected(contextRoot);

        var authorityRoot = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun()));
        var authorityEntry = Authorities(authorityRoot)[0]!.AsObject();
        var reorderedAuthority = ReverseProperties(authorityEntry["authority"]!.AsObject());
        authorityEntry["sha256"] = Hash(Encoding.UTF8.GetBytes(reorderedAuthority.ToJsonString()));
        authorityEntry["authority"] = reorderedAuthority;
        AssertCanonicalTypedOrderRejected(authorityRoot);

        var governanceRoot = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun()));
        var governanceEvidence = ToolEvidence(governanceRoot, 3);
        governanceEvidence["governance"] = ReverseProperties(governanceEvidence["governance"]!.AsObject());
        AssertCanonicalTypedOrderRejected(governanceRoot);
    }

    [Fact]
    public void Artifact_deserializer_rejects_noncanonical_compact_tool_enum_spellings()
    {
        var commandRoot = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun()));
        var requestEntry = ToolRequests(commandRoot)[0]!.AsObject();
        var request = requestEntry["toolRequest"]!.AsObject();
        request["command"] = "Search";
        requestEntry["sha256"] = Hash(Encoding.UTF8.GetBytes(request.ToJsonString()));
        AssertCanonicalEnumSpellingRejected(commandRoot);

        var outcomeRoot = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun()));
        var outcome = Events(outcomeRoot)
            .Select(item => item!["toolEvidence"] as JsonObject)
            .Single(evidence => evidence?["shape"]?.GetValue<int>() == 3)!;
        outcome["outcome"] = "Succeeded";
        AssertCanonicalEnumSpellingRejected(outcomeRoot);

        foreach (var shape in new[] { 2, 3, 4, 5 })
        {
            var phaseRoot = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun(includeIntegrity: true)));
            var evidence = Events(phaseRoot)
                .Select(item => item!["toolEvidence"] as JsonObject)
                .Single(candidate => candidate?["shape"]?.GetValue<int>() == shape)!;
            var phase = evidence["phase"]!.GetValue<string>();
            evidence["phase"] = char.ToUpperInvariant(phase[0]) + phase[1..];
            AssertCanonicalEnumSpellingRejected(phaseRoot);
        }
    }

    [Fact]
    public void Artifact_deserializer_rejects_present_properties_that_the_typed_serializer_ignores()
    {
        var root = Parse(Artifact());
        root["run"]!["isTerminal"] = false;

        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")));

        Assert.Contains("is omitted by the canonical serializer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_evidence_artifact_round_trips_all_success_phases_and_integrity_markers()
    {
        var run = CreateToolRun(includeIntegrity: true);
        var validation = CustomLoopRunValidator.Validate(run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(artifact);

        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(hydrated));
        Assert.Equal(artifact, CustomLoopRunArtifactSerializer.Serialize(hydrated));
        var legacyIntegrity = Assert.Single(hydrated.Events, item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.IntegrityFailed).ToolEvidence!;
        Assert.NotNull(legacyIntegrity.BrokerRequestId);
        Assert.NotNull(legacyIntegrity.Governance);
        Assert.NotNull(legacyIntegrity.Outcome);
        Assert.NotNull(legacyIntegrity.CanonicalResultReturnedToModel);
        Assert.Equal(CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes, legacyIntegrity.ReservedUtf8Bytes);
    }

    [Fact]
    public async Task Run_store_accounts_for_completed_and_standalone_tool_evidence_lifecycles()
    {
        using var completedWorkspace = new TestWorkspace();
        var completedStore = new CustomLoopRunStore(new WorkspacePaths(completedWorkspace.RootPath));
        var completed = CreateToolRun();
        var completedInitial = CustomLoopAdmissionRequestHash.Apply(completed with
        {
            LifecycleVersion = 1,
            Events = [completed.Events[0]],
            Checkpoint = CustomLoopRunCheckpoint.Start()
        });

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await completedStore.CreateAsync(completedInitial)).Status);
        Assert.True(await completedStore.HasSufficientTraceCapacityForDispatchAsync(completed, completedInitial.LifecycleVersion));
        using var missingWorkspace = new TestWorkspace();
        Assert.True(await new CustomLoopRunStore(new WorkspacePaths(missingWorkspace.RootPath)).HasSufficientTraceCapacityForDispatchAsync(completed, completedInitial.LifecycleVersion));
        var completedCurrent = completedInitial;
        for (var eventCount = 2; eventCount <= completed.Events.Length; eventCount++)
        {
            var next = completed with
            {
                LifecycleVersion = completedCurrent.LifecycleVersion + 1,
                Events = completed.Events.Take(eventCount).ToArray(),
                Checkpoint = eventCount < 3 ? CustomLoopRunCheckpoint.Start() : completed.Checkpoint
            };
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await completedStore.UpdateAsync(next, completedCurrent.LifecycleVersion)).Status);
            completedCurrent = next;
        }
        var completedQuota = await completedStore.GetTraceQuotaAsync();
        var completedReloaded = Assert.IsType<CustomLoopRunRecord>(await completedStore.GetAsync(completed.Id));

        Assert.Equal(completed.Events.Length, completedReloaded.Events.Length);
        Assert.True(await completedStore.HasSufficientTraceCapacityForDispatchAsync(completed, completed.LifecycleVersion));
        Assert.True(await completedStore.HasSufficientTraceCapacityForDispatchAsync(completed, completed.LifecycleVersion - 1));
        Assert.Equal(1, completedQuota.ActiveReservationCount);
        Assert.True(completedQuota.AccountedTraceUtf8Bytes > 0);

        using var integrityWorkspace = new TestWorkspace();
        var integrityStore = new CustomLoopRunStore(new WorkspacePaths(integrityWorkspace.RootPath));
        var integrity = CreateStandaloneRepeatedIntegrityRun();
        var integrityInitial = CustomLoopAdmissionRequestHash.Apply(integrity with
        {
            LifecycleVersion = 1,
            Events = [integrity.Events[0]],
            Checkpoint = CustomLoopRunCheckpoint.Start()
        });

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await integrityStore.CreateAsync(integrityInitial)).Status);
        var integrityCurrent = integrityInitial;
        for (var eventCount = 2; eventCount <= integrity.Events.Length; eventCount++)
        {
            var next = integrity with
            {
                LifecycleVersion = integrityCurrent.LifecycleVersion + 1,
                Events = integrity.Events.Take(eventCount).ToArray(),
                Checkpoint = eventCount < 3 ? CustomLoopRunCheckpoint.Start() : integrity.Checkpoint
            };
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await integrityStore.UpdateAsync(next, integrityCurrent.LifecycleVersion)).Status);
            integrityCurrent = next;
        }
        var integrityQuota = await integrityStore.GetTraceQuotaAsync();

        Assert.Equal(integrity.Events.Length, (await integrityStore.GetAsync(integrity.Id))!.Events.Length);
        Assert.Equal(1, integrityQuota.ActiveReservationCount);
        Assert.True(integrityQuota.AccountedTraceUtf8Bytes > 0);

        using var legacyWorkspace = new TestWorkspace();
        var legacyStore = new CustomLoopRunStore(new WorkspacePaths(legacyWorkspace.RootPath));
        var legacy = CreateToolRun(includeIntegrity: true) with { Id = "run-tool-legacy", AdmissionOperationId = "invoke-tool-legacy" };
        legacy = CustomLoopAdmissionRequestHash.Apply(legacy with { AdmissionRequestHash = string.Empty });
        var legacyInitial = CustomLoopAdmissionRequestHash.Apply(legacy with
        {
            LifecycleVersion = 1,
            Events = [legacy.Events[0]],
            Checkpoint = CustomLoopRunCheckpoint.Start()
        });

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await legacyStore.CreateAsync(legacyInitial)).Status);
        var legacyCurrent = legacyInitial;
        for (var eventCount = 2; eventCount <= legacy.Events.Length; eventCount++)
        {
            var next = legacy with
            {
                LifecycleVersion = legacyCurrent.LifecycleVersion + 1,
                Events = legacy.Events.Take(eventCount).ToArray(),
                Checkpoint = eventCount < 3 ? CustomLoopRunCheckpoint.Start() : legacy.Checkpoint
            };
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await legacyStore.UpdateAsync(next, legacyCurrent.LifecycleVersion)).Status);
            legacyCurrent = next;
        }

        Assert.True((await legacyStore.GetTraceQuotaAsync()).AccountedTraceUtf8Bytes > 0);
    }

    [Fact]
    public void Tool_evidence_artifact_round_trips_refreshed_pre_actuation_authority()
    {
        var run = CreateToolRun();
        var refreshed = ToolAuthority() with
        {
            RoleId = "replacement-role",
            CurrentRoleCeiling = [],
            EffectiveAssignments = [],
            RoleCeilingHash = CustomLoopTraceContentHash.Compute("replacement-role"),
            IsValid = false,
            Detail = "The admitted directory role changed before actuation."
        };
        var events = run.Events.Select((item, index) => index is 3 or 4 or 5
            ? item with { ToolAuthority = refreshed, ToolEvidence = item.ToolEvidence! with { Authority = refreshed } }
            : item).ToArray();
        run = run with { Events = events };
        var validation = CustomLoopRunValidator.Validate(run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
        var root = Parse(artifact);
        Assert.Equal(2, Authorities(root).Count);
        Assert.NotEqual(
            Events(root)[2]!["toolAuthority"]!["$authority"]!.GetValue<string>(),
            Events(root)[3]!["toolAuthority"]!["$authority"]!.GetValue<string>());

        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(artifact);

        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(hydrated));
        Assert.Equal(artifact, CustomLoopRunArtifactSerializer.Serialize(hydrated));
        Assert.Equal("default-role", hydrated.Events[2].ToolEvidence!.Authority.RoleId);
        Assert.All(hydrated.Events.Skip(3).Take(3), item => Assert.Equal("replacement-role", item.ToolEvidence!.Authority.RoleId));
    }

    [Fact]
    public void Tool_evidence_artifact_round_trips_one_exact_standalone_repeated_request_integrity_record()
    {
        var run = CreateStandaloneRepeatedIntegrityRun();
        var validation = CustomLoopRunValidator.Validate(run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
        var root = Parse(artifact);
        Assert.Equal(6, ToolEvidence(root, 6)["shape"]!.GetValue<int>());

        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(artifact);

        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(hydrated));
        Assert.Equal(artifact, CustomLoopRunArtifactSerializer.Serialize(hydrated));
        var integrity = Assert.Single(hydrated.Events, item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.IntegrityFailed).ToolEvidence!;
        Assert.Equal(2, integrity.RequestOrdinal);
        Assert.Equal("request-correlation-1", integrity.RequestCorrelationId);
        Assert.Equal("shared/repeated.txt", integrity.TargetPath);
        Assert.Equal(CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes, integrity.ReservedUtf8Bytes);
    }

    [Fact]
    public void Artifact_serializer_rejects_duplicate_correlation_ids_across_distinct_request_reservations()
    {
        var run = CreateToolRun();
        var reservationEvent = Assert.Single(run.Events, item => item.Kind == CustomLoopRunEventKind.ToolRequestReserved);
        var reservation = Assert.IsType<CustomLoopToolTraceEvidence>(reservationEvent.ToolEvidence);
        var duplicateEvidence = reservation with { RequestOrdinal = 2, TargetPath = "shared/duplicate.txt" };
        var duplicateEvent = ToolEvent(run.Events.Length + 1, "event-duplicate-correlation", CustomLoopRunEventKind.ToolRequestReserved, duplicateEvidence, reservation.Authority);
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Events = [.. run.Events, duplicateEvent]
        };

        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Serialize(candidate));

        Assert.Contains("unique exact request-and-authority owner", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_validator_requires_the_standalone_integrity_reservation_class()
    {
        var run = CreateStandaloneRepeatedIntegrityRun();
        var eventIndex = Array.FindIndex(run.Events, item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.IntegrityFailed);
        var invalid = run with
        {
            Events = run.Events.Select((item, index) => index == eventIndex
                ? item with { ToolEvidence = item.ToolEvidence! with { ReservedUtf8Bytes = CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes } }
                : item).ToArray()
        };

        var validation = CustomLoopRunValidator.Validate(invalid);

        Assert.Contains(validation.Errors, error => error.Code == "invalid_tool_integrity_reservation");
    }

    [Fact]
    public void Tool_evidence_artifact_round_trips_decomposed_unicode_target_paths_exactly()
    {
        const string DecomposedPath = "shared/cafe\u0301.txt";
        var run = CreateToolRun(targetPath: DecomposedPath);

        var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(artifact);

        Assert.All(hydrated.Events.Where(item => item.ToolEvidence is not null), item => Assert.Equal(DecomposedPath, item.ToolEvidence!.TargetPath));
        Assert.Equal(artifact, CustomLoopRunArtifactSerializer.Serialize(hydrated));
    }

    [Fact]
    public void Artifact_serializer_rejects_cross_event_tool_protocol_mismatches()
    {
        var run = CreateToolRun();
        RejectRun(RemoveEvents(run, 2));
        RejectRun(InsertEvent(run, 3, run.Events[2] with { EventId = "event-reservation-duplicate" }));
        RejectRun(ReplaceEvidence(run, 3, evidence => evidence with { TargetPath = "different-target" }));

        var differentAuthority = ToolAuthority() with { Detail = "Different current authority detail." };
        RejectRun(ReplaceEvidence(run, 4, evidence => evidence with { Authority = differentAuthority }, differentAuthority));
        RejectRun(InsertEvent(run, 4, run.Events[3] with { EventId = "event-governance-duplicate" }));
        RejectRun(RemoveEvents(run, 3));
        RejectRun(RemoveEvents(run, 4));
        RejectRun(ReplaceEvidence(run, 5, evidence => evidence with { Governance = evidence.Governance! with { ApprovalDetail = "Mismatched approval detail." } }));

        var integrity = CreateToolRun(includeIntegrity: true);
        RejectRun(ReplaceEvidence(integrity, 6, evidence => evidence with { CanonicalResultHash = new string('0', 64) }));
        RejectRun(InsertEvent(integrity, integrity.Events.Length, integrity.Events[3] with { EventId = "event-governance-after-integrity" }));
        RejectRun(InsertEvent(integrity, integrity.Events.Length, integrity.Events[^1] with { EventId = "event-integrity-duplicate" }));
    }

    [Fact]
    public void Artifact_deserializer_rejects_compact_tool_protocol_order_shape_and_reference_mismatches()
    {
        RejectTool(root => ToolEvidence(root, 2)["phase"] = "governanceDecided");
        RejectTool(root =>
        {
            var events = Events(root);
            var governance = events[3]!.DeepClone();
            events.RemoveAt(3);
            events.Insert(2, governance);
        });
        RejectTool(root => Events(root).Insert(4, Events(root)[3]!.DeepClone()));
        RejectTool(root => Events(root).RemoveAt(3));
        RejectTool(root => Events(root).RemoveAt(4));
        RejectTool(root => ToolEvidence(root, 5)["outcomeSequence"] = 999);
        RejectTool(root => ToolEvidence(root, 5)["shape"] = 99);
        RejectTool(root => ToolEvidence(root, 3)["extra"] = true);
        RejectTool(root => ToolEvidence(root, 2)["shape"] = "one");

        RejectTool(root =>
        {
            var authorities = Authorities(root);
            var authority = authorities[0]!["authority"]!.DeepClone().AsObject();
            authority["isValid"] = false;
            authorities.Add(StructuralEntry("a1", "authority", authority));
            Events(root)[4]!["toolAuthority"] = new JsonObject { ["$authority"] = "a1" };
        });
    }

    [Fact]
    public void Artifact_deserializer_rejects_malformed_or_dangling_compact_integrity_markers()
    {
        RejectTool(root => ToolEvidence(root, 6)["hasGovernance"] = "yes", includeIntegrity: true);
        RejectTool(root =>
        {
            var events = Events(root);
            events.RemoveAt(5);
            events.RemoveAt(4);
            events.RemoveAt(3);
            var marker = ToolEvidence(root, 3);
            marker["hasGovernance"] = true;
            marker["hasOutcome"] = true;
            marker["hasCanonicalResult"] = true;
        }, includeIntegrity: true);
        RejectTool(root => AppendCompactToolEvent(root, 3, "event-governance-after-integrity"), includeIntegrity: true);
        RejectTool(root => AppendCompactToolEvent(root, 6, "event-integrity-duplicate"), includeIntegrity: true);
    }

    [Fact]
    public void Artifact_deserializer_rejects_malformed_or_duplicate_standalone_integrity_owners()
    {
        var artifact = CustomLoopRunArtifactSerializer.Serialize(CreateStandaloneRepeatedIntegrityRun());

        Reject(root => ToolEvidence(root, 6)["brokerRequestId"] = "unexpected-broker", artifact);
        Reject(root => ToolEvidence(root, 6)["phase"] = "requestReserved", artifact);
        Reject(root => ToolEvidence(root, 6)["toolRequest"] = new JsonObject { ["$toolRequest"] = "q0" }, artifact);
    }

    private static byte[] Artifact() => CustomLoopRunArtifactSerializer.Serialize(CreateRun());

    private static CustomLoopControlOperation PendingControl(string operationId, string actor)
    {
        const string RunId = "run-control-boundary";
        const int ExpectedVersion = 2;
        const CustomLoopControlKind Kind = CustomLoopControlKind.Pause;
        return new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(Kind, RunId, ExpectedVersion, operationId, actor),
            Kind,
            RunId,
            ExpectedVersion,
            actor,
            _timestamp,
            _timestamp,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "Control operation is pending.");
    }

    private static CustomLoopControlOperation CompleteControl(CustomLoopControlOperation pending)
    {
        return pending with
        {
            UpdatedAtUtc = pending.UpdatedAtUtc.AddSeconds(1),
            State = CustomLoopControlOperationState.Complete,
            Outcome = CustomLoopControlStatus.Paused,
            ResultLifecycleVersion = pending.ExpectedLifecycleVersion,
            ResultRunStatus = CustomLoopRunStatus.Paused,
            OutcomeAuditRecorded = true,
            Detail = "Control operation completed."
        };
    }

    private static CustomLoopRunRecord CreateRun()
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-boundary", "default-role", "step-1", "create-loop", _timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(_timestamp);
        var admitted = new CustomLoopRunEvent(1, "event-1", _timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-boundary", definition.Id, 1, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-boundary", "test-user", string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null) { CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp) };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord CreateToolRun(bool includeIntegrity = false, string targetPath = ".")
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-tool-boundary", "default-role", "step-1", "create-tool-loop", _timestamp);
        var definition = seed with { ToolAssignments = [CustomLoopToolAssignment.Search], ContentHash = string.Empty };
        definition = CustomLoopDefinitionContentHash.Apply(definition with { CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(definition.Id, definition.ToolAssignments) });
        var authority = ToolAuthority();
        var governance = new ToolGovernanceEvidence(
            ToolAuthorityDecision.Allowed,
            "Current authority allowed the request.",
            PermissionDecision.Allow,
            ".",
            "Permission policy allowed the request.",
            CustomLoopTraceContentHash.Compute("permission-policy"),
            ToolApprovalDecision.NotRequired,
            null,
            "Approval was not required.");
        const string Canonical = "search-result";
        var canonicalHash = CustomLoopTraceContentHash.Compute(Canonical);
        var reservation = ToolEvidence(CustomLoopToolEvidencePhase.RequestReserved, null, null, null, null, null, null, false, authority, targetPath);
        var governed = ToolEvidence(CustomLoopToolEvidencePhase.GovernanceDecided, "broker-1", governance, null, null, null, null, false, authority, targetPath);
        var outcome = ToolEvidence(CustomLoopToolEvidencePhase.OutcomeObserved, "broker-1", governance, ToolExecutionOutcome.Succeeded, Canonical, canonicalHash, Canonical.Length, false, authority, targetPath);
        var returned = ToolEvidence(CustomLoopToolEvidencePhase.OutcomeObserved, "broker-1", governance, ToolExecutionOutcome.Succeeded, Canonical, canonicalHash, Canonical.Length, true, authority, targetPath);
        var events = new List<CustomLoopRunEvent>
        {
            new(1, "event-admitted", _timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null, authority),
            new(2, "event-attempt-start", _timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Inference attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "response-1", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes),
            ToolEvent(3, "event-reservation", CustomLoopRunEventKind.ToolRequestReserved, reservation, authority),
            ToolEvent(4, "event-governance", CustomLoopRunEventKind.ToolGovernanceDecided, governed, authority),
            ToolEvent(5, "event-outcome", CustomLoopRunEventKind.ToolOutcomeObserved, outcome, authority),
            ToolEvent(6, "event-returned", CustomLoopRunEventKind.ToolOutcomeObserved, returned, authority)
        };
        if (includeIntegrity)
        {
            var integrity = returned with { Phase = CustomLoopToolEvidencePhase.IntegrityFailed, ReturnedToModel = false };
            events.Add(ToolEvent(7, "event-integrity", CustomLoopRunEventKind.ToolIntegrityFailed, integrity, authority));
        }

        var checkpoint = CustomLoopRunCheckpoint.Start() with { ToolRequestsUsed = 1 };
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-tool-boundary", definition.Id, events.Count, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-tool-boundary", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(_timestamp), CustomLoopExecutionClock.NotStarted(), checkpoint, events.ToArray(), null, null, null) { CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp) };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord CreateStandaloneRepeatedIntegrityRun()
    {
        var run = CreateToolRun();
        var authority = run.Events[1].ToolAuthority!;
        var integrity = ToolEvidence(
            CustomLoopToolEvidencePhase.IntegrityFailed,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            authority,
            "shared/repeated.txt") with
        {
            RequestOrdinal = 2,
            ReservedUtf8Bytes = CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes
        };
        var events = run.Events.Append(ToolEvent(7, "event-repeated-integrity", CustomLoopRunEventKind.ToolIntegrityFailed, integrity, authority)).ToArray();
        return run with
        {
            LifecycleVersion = events.Length,
            Events = events
        };
    }

    private static CustomLoopToolAuthoritySnapshot ToolAuthority()
    {
        var assignments = new[] { CustomLoopToolAssignment.Search };
        return new CustomLoopToolAuthoritySnapshot(
            "default-role",
            assignments,
            assignments,
            assignments,
            assignments,
            CustomLoopTraceContentHash.Compute("role-ceiling"),
            CustomLoopTraceContentHash.Compute("tool-catalog"),
            _timestamp,
            true,
            "Current role and implemented catalog allow search.");
    }

    private static CustomLoopToolTraceEvidence ToolEvidence(
        CustomLoopToolEvidencePhase phase,
        string? brokerRequestId,
        ToolGovernanceEvidence? governance,
        ToolExecutionOutcome? outcome,
        string? canonical,
        string? canonicalHash,
        int? canonicalCharacters,
        bool returned,
        CustomLoopToolAuthoritySnapshot authority,
        string targetPath)
    {
        return new CustomLoopToolTraceEvidence(phase, 1, "request-correlation-1", brokerRequestId, ToolCommand.Search, targetPath, null, "*.cs", workspaceResolvedTarget(), authority, governance, outcome, canonical, canonicalHash, canonicalCharacters, returned, CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);

        static string workspaceResolvedTarget() => "workspace/search";
    }

    private static CustomLoopRunEvent ToolEvent(long sequence, string eventId, CustomLoopRunEventKind kind, CustomLoopToolTraceEvidence evidence, CustomLoopToolAuthoritySnapshot authority)
    {
        return new CustomLoopRunEvent(sequence, eventId, _timestamp, kind, 1, "step-1", 1, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null, authority, evidence);
    }

    private static CustomLoopRunRecord ReplaceEvidence(CustomLoopRunRecord run, int eventIndex, Func<CustomLoopToolTraceEvidence, CustomLoopToolTraceEvidence> mutate, CustomLoopToolAuthoritySnapshot? eventAuthority = null)
    {
        var events = run.Events.ToArray();
        var item = events[eventIndex];
        events[eventIndex] = item with { ToolEvidence = mutate(item.ToolEvidence!), ToolAuthority = eventAuthority ?? item.ToolAuthority };
        return run with { Events = events };
    }

    private static CustomLoopRunRecord RemoveEvents(CustomLoopRunRecord run, params int[] eventIndexes)
    {
        var removed = eventIndexes.ToHashSet();
        var events = run.Events.Where((_, index) => !removed.Contains(index)).ToArray();
        return run with { Events = Renumber(events), LifecycleVersion = events.Length };
    }

    private static CustomLoopRunRecord InsertEvent(CustomLoopRunRecord run, int eventIndex, CustomLoopRunEvent item)
    {
        var events = run.Events.ToList();
        events.Insert(eventIndex, item);
        return run with { Events = Renumber(events), LifecycleVersion = events.Count };
    }

    private static CustomLoopRunEvent[] Renumber(IEnumerable<CustomLoopRunEvent> events) => events.Select((item, index) => item with { Sequence = index + 1L }).ToArray();

    private static void RejectRun(CustomLoopRunRecord run)
    {
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Serialize(run));
    }

    private static void RejectTool(Action<JsonObject> mutate, bool includeIntegrity = false)
    {
        var root = Parse(CustomLoopRunArtifactSerializer.Serialize(CreateToolRun(includeIntegrity)));
        mutate(root);
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static void Reject(Action<JsonObject> mutate, byte[]? artifact = null)
    {
        var root = Parse(artifact ?? Artifact());
        mutate(root);
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static JsonObject Parse(byte[] artifact) => JsonNode.Parse(artifact)!.AsObject();

    private static JsonArray Content(JsonObject root) => root["content"]!.AsArray();

    private static JsonObject Entry(JsonObject root) => Content(root)[0]!.AsObject();

    private static JsonArray Blocks(JsonObject root) => root["contextBlocks"]!.AsArray();

    private static JsonArray Authorities(JsonObject root) => root["authorities"]!.AsArray();

    private static JsonArray ToolRequests(JsonObject root) => root["toolRequests"]!.AsArray();

    private static JsonArray Events(JsonObject root) => root["run"]!["events"]!.AsArray();

    private static JsonObject ToolEvidence(JsonObject root, int eventIndex) => Events(root)[eventIndex]!["toolEvidence"]!.AsObject();

    private static void AssertCanonicalEnumSpellingRejected(JsonObject root)
    {
        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")));
        Assert.Contains("does not use its canonical serializer spelling", exception.Message, StringComparison.Ordinal);
    }

    private static void AppendCompactToolEvent(JsonObject root, int sourceIndex, string eventId)
    {
        var events = Events(root);
        var appended = events[sourceIndex]!.DeepClone().AsObject();
        appended["sequence"] = events.Count + 1;
        appended["eventId"] = eventId;
        events.Add(appended);
    }

    private static JsonObject FirstEvent(JsonObject root) => root["run"]!["events"]![0]!.AsObject();

    private static JsonObject ContentEntry(string id, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new JsonObject
        {
            ["id"] = id,
            ["sha256"] = Hash(bytes),
            ["utf16Characters"] = text.Length,
            ["utf8Bytes"] = bytes.Length,
            ["base64"] = Convert.ToBase64String(bytes)
        };
    }

    private static JsonObject StructuralEntry(string id, string propertyName, JsonObject value, string? hash = null)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["sha256"] = hash ?? Hash(Encoding.UTF8.GetBytes(value.ToJsonString())),
            [propertyName] = value
        };
    }

    private static JsonObject ReverseProperties(JsonObject value)
    {
        var reordered = new JsonObject();
        foreach (var property in value.Reverse())
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        return reordered;
    }

    private static void AssertCanonicalTypedOrderRejected(JsonObject root)
    {
        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")));
        Assert.Contains("not in canonical serializer order", exception.Message, StringComparison.Ordinal);
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string NestedJson(int depth) => string.Concat(Enumerable.Repeat("{\"nested\":", depth)) + "null" + new string('}', depth);

    private static string IndexedId(string prefix, int index)
    {
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        do
        {
            buffer[--position] = Digits[index % 36];
            index /= 36;
        }
        while (index > 0);

        return prefix + new string(buffer[position..]);
    }

}
