using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class PersistencePublicBoundaryCoverageTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

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
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
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
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopControlOperationsPath, "control-corrupt.json"), "{ malformed");
        await Assert.ThrowsAsync<FormatException>(() => store.GetAsync("control-corrupt"));

        var pending = PendingControl("control-embedded", "web");
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
    public void Tool_evidence_artifact_round_trips_all_success_phases_and_integrity_markers()
    {
        var run = CreateToolRun(includeIntegrity: true);
        var validation = CustomLoopRunValidator.Validate(run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(artifact);

        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(hydrated));
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
        RejectRun(ReplaceEvidence(run, 3, evidence => evidence with { Authority = differentAuthority }, differentAuthority));
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
            Events(root)[3]!["toolAuthority"] = new JsonObject { ["$authority"] = "a1" };
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

    private static byte[] Artifact() => CustomLoopRunArtifactSerializer.Serialize(CreateRun());

    private static CustomLoopControlOperation PendingControl(string operationId, string actor)
    {
        const string runId = "run-control-boundary";
        const int expectedVersion = 2;
        const CustomLoopControlKind kind = CustomLoopControlKind.Pause;
        return new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor),
            kind,
            runId,
            expectedVersion,
            actor,
            Timestamp,
            Timestamp,
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
            UpdatedAtUtc = Timestamp.AddSeconds(1),
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
        var definition = CustomLoopDefinition.CreateSeed("loop-boundary", "default-role", "step-1", "create-loop", Timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(Timestamp);
        var admitted = new CustomLoopRunEvent(1, "event-1", Timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-boundary", definition.Id, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-boundary", "test-user", string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord CreateToolRun(bool includeIntegrity = false)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-tool-boundary", "default-role", "step-1", "create-tool-loop", Timestamp);
        var definition = CustomLoopDefinitionContentHash.Apply(seed with { ToolAssignments = [CustomLoopToolAssignment.Search], ContentHash = string.Empty });
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
        const string canonical = "search-result";
        var canonicalHash = CustomLoopTraceContentHash.Compute(canonical);
        var reservation = ToolEvidence(CustomLoopToolEvidencePhase.RequestReserved, null, null, null, null, null, null, false, authority);
        var governed = ToolEvidence(CustomLoopToolEvidencePhase.GovernanceDecided, "broker-1", governance, null, null, null, null, false, authority);
        var outcome = ToolEvidence(CustomLoopToolEvidencePhase.OutcomeObserved, "broker-1", governance, ToolExecutionOutcome.Succeeded, canonical, canonicalHash, canonical.Length, false, authority);
        var returned = ToolEvidence(CustomLoopToolEvidencePhase.OutcomeObserved, "broker-1", governance, ToolExecutionOutcome.Succeeded, canonical, canonicalHash, canonical.Length, true, authority);
        var events = new List<CustomLoopRunEvent>
        {
            new(1, "event-admitted", Timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null, authority),
            new(2, "event-attempt-start", Timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Inference attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "response-1", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes),
            ToolEvent(3, "event-reservation", CustomLoopRunEventKind.ToolRequestReserved, reservation, authority),
            ToolEvent(4, "event-governance", CustomLoopRunEventKind.ToolGovernanceDecided, governed, authority),
            ToolEvent(5, "event-outcome", CustomLoopRunEventKind.ToolOutcomeObserved, outcome, authority),
            ToolEvent(6, "event-returned", CustomLoopRunEventKind.ToolOutcomeObserved, returned, authority)
        };
        if (includeIntegrity)
        {
            var integrity = ToolEvidence(CustomLoopToolEvidencePhase.IntegrityFailed, "broker-1", governance, ToolExecutionOutcome.Succeeded, canonical, canonicalHash, canonical.Length, false, authority);
            events.Add(ToolEvent(7, "event-integrity", CustomLoopRunEventKind.ToolIntegrityFailed, integrity, authority));
        }

        var checkpoint = CustomLoopRunCheckpoint.Start() with { ToolRequestsUsed = 1 };
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-tool-boundary", definition.Id, events.Count, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-tool-boundary", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(Timestamp), CustomLoopExecutionClock.NotStarted(), checkpoint, events.ToArray(), null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
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
            Timestamp,
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
        CustomLoopToolAuthoritySnapshot authority)
    {
        return new CustomLoopToolTraceEvidence(phase, 1, "request-correlation-1", brokerRequestId, ToolCommand.Search, ".", null, "*.cs", workspaceResolvedTarget(), authority, governance, outcome, canonical, canonicalHash, canonicalCharacters, returned, CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);

        static string workspaceResolvedTarget() => "workspace/search";
    }

    private static CustomLoopRunEvent ToolEvent(long sequence, string eventId, CustomLoopRunEventKind kind, CustomLoopToolTraceEvidence evidence, CustomLoopToolAuthoritySnapshot authority)
    {
        return new CustomLoopRunEvent(sequence, eventId, Timestamp, kind, 1, "step-1", 1, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null, authority, evidence);
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

    private static void Reject(Action<JsonObject> mutate)
    {
        var root = Parse(Artifact());
        mutate(root);
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static JsonObject Parse(byte[] artifact) => JsonNode.Parse(artifact)!.AsObject();

    private static JsonArray Content(JsonObject root) => root["content"]!.AsArray();

    private static JsonObject Entry(JsonObject root) => Content(root)[0]!.AsObject();

    private static JsonArray Blocks(JsonObject root) => root["contextBlocks"]!.AsArray();

    private static JsonArray Authorities(JsonObject root) => root["authorities"]!.AsArray();

    private static JsonArray Events(JsonObject root) => root["run"]!["events"]!.AsArray();

    private static JsonObject ToolEvidence(JsonObject root, int eventIndex) => Events(root)[eventIndex]!["toolEvidence"]!.AsObject();

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

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string NestedJson(int depth) => string.Concat(Enumerable.Repeat("{\"nested\":", depth)) + "null" + new string('}', depth);

    private static string IndexedId(string prefix, int index)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        do
        {
            buffer[--position] = digits[index % 36];
            index /= 36;
        }
        while (index > 0);

        return prefix + new string(buffer[position..]);
    }

}
