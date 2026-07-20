using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using Xunit.Abstractions;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopRunArtifactMaximumShapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false, MaxDepth = 64 };
    private readonly ITestOutputHelper _output;

    public CustomLoopRunArtifactMaximumShapeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Public_artifact_contract_round_trips_the_maximum_bounded_shape_below_fifteen_mebibytes()
    {
        var fixture = await GenerateMaximumExecutionAsync();
        var terminal = fixture.Execution.Run!;
        Assert.Equal(CustomLoopOrderedRunStatus.Completed, fixture.Execution.Status);
        Assert.True(CustomLoopRunValidator.Validate(terminal).IsValid, Format(CustomLoopRunValidator.Validate(terminal).Errors));
        Assert.Equal(CustomLoopLimits.MaxModelAttemptsPerRun, fixture.Executor.Requests.Count);
        Assert.Equal(55, fixture.Executor.Requests.Count(request => !request.IsExit));
        Assert.Equal(10, fixture.Executor.Requests.Count(request => request.IsExit));
        Assert.Equal(CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun, terminal.Checkpoint.ToolRequestsUsed);
        Assert.Equal(CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun, terminal.Events.Count(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.RequestReserved));
        var governanceEvents = terminal.Events.Where(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.GovernanceDecided).ToArray();
        Assert.Equal(CustomLoopLimits.MaxGovernedToolRequestsPerRun, governanceEvents.Count(item => item.ToolEvidence!.Governance!.AuthorityDecision == ToolAuthorityDecision.Allowed));
        var deniedGovernance = Assert.Single(governanceEvents, item => item.ToolEvidence!.Governance!.AuthorityDecision == ToolAuthorityDecision.Denied);
        var deniedCorrelationId = deniedGovernance.ToolEvidence!.RequestCorrelationId;
        var deniedProtocol = terminal.Events.Where(item => string.Equals(item.ToolEvidence?.RequestCorrelationId, deniedCorrelationId, StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, deniedProtocol.Length);
        Assert.All(deniedProtocol.Skip(1), item => Assert.Equal(ToolAuthorityDecision.Denied, item.ToolEvidence!.Governance!.AuthorityDecision));
        Assert.All(deniedProtocol.Skip(2), item => Assert.Equal(ToolExecutionOutcome.Denied, item.ToolEvidence!.Outcome));
        Assert.True(deniedProtocol[^1].ToolEvidence!.ReturnedToModel);
        var durableOutcomes = terminal.Events.Where(item => item.ToolEvidence is { Phase: CustomLoopToolEvidencePhase.OutcomeObserved, ReturnedToModel: false }).ToArray();
        Assert.Equal(CustomLoopLimits.MaxGovernedToolRequestsPerRun, durableOutcomes.Count(item => item.ToolEvidence!.Outcome == ToolExecutionOutcome.Succeeded));
        Assert.Single(durableOutcomes, item => item.ToolEvidence!.Outcome == ToolExecutionOutcome.Denied);
        Assert.Equal(CustomLoopLimits.MaxModelAttemptsPerRun, terminal.Events.Count(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved));
        var expectedPublications = 2 * CustomLoopLimits.MaxConversationPublicationEffectsPerRun;
        Assert.Equal(expectedPublications, terminal.Events.Count(item => item.Kind is CustomLoopRunEventKind.ConversationPublicationStarted or CustomLoopRunEventKind.ConversationPublished));
        Assert.Equal(CustomLoopLimits.MaxConversationPublicationEffectsPerRun, fixture.Publisher.Requests.Count);
        Assert.Equal(CustomLoopLimits.MaxConversationPublicationEffectsPerRun - 1, fixture.Publisher.Requests[^1].PriorPublications!.Count);
        Assert.InRange(terminal.Events.Length, 1, CustomLoopLimits.MaxTraceEventsPerRun);
        await AssertPublicStoreReservationClassesAsync(fixture);

        var encoded = CustomLoopRunArtifactSerializer.Serialize(terminal);
        _output.WriteLine($"MAX_ARTIFACT_BYTES={encoded.Length}");
        WriteEnvelopeByteBreakdown(encoded);
        Assert.True(encoded.Length <= 15 * 1024 * 1024, $"Maximum production artifact was {encoded.Length:N0} bytes.");
        Assert.True(encoded.Length <= CustomLoopLimits.MaxRunTraceUtf8Bytes);
        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(encoded);
        Assert.Equal(JsonSerializer.Serialize(terminal), JsonSerializer.Serialize(hydrated));
        Assert.Equal(encoded, CustomLoopRunArtifactSerializer.Serialize(hydrated));

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var artifactPath = await WriteArtifactAsync(paths, terminal, encoded);
        var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var publicReload = await restarted.GetAsync(terminal.Id);
        Assert.NotNull(publicReload);
        Assert.Equal(JsonSerializer.Serialize(terminal), JsonSerializer.Serialize(publicReload));
        Assert.Equal(terminal.Events.Length, publicReload.Events.Length);
        Assert.Equal(terminal.FinalOutput, publicReload.FinalOutput);
        Assert.Equal(encoded, await File.ReadAllBytesAsync(artifactPath));
        var inspection = await restarted.InspectTraceAsync(terminal.Id);
        Assert.NotNull(inspection);
        Assert.Equal(encoded.Length, inspection.PersistedArtifactUtf8Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant(), inspection.PersistedArtifactHash);
        var quota = await restarted.GetTraceQuotaAsync();
        Assert.Equal(encoded.Length, quota.ActualTraceUtf8Bytes);
        Assert.Equal(encoded.Length + CustomLoopLimits.MaxTraceControlEventUtf8Bytes, quota.AccountedTraceUtf8Bytes);
        Assert.Equal(1, quota.ActiveReservationCount);
        Assert.Equal(CustomLoopLimits.MaxTraceControlEventUtf8Bytes, quota.ReservedCapacityUtf8Bytes);

        AssertCanonicalReferences(encoded);
        var orderingRun = WithSecondAuthority(fixture.Store.Transitions.Select(transition => transition.Candidate).First(run => run.Events.Count(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.RequestReserved) >= 2));
        var orderingValidation = CustomLoopRunValidator.Validate(orderingRun);
        Assert.True(orderingValidation.IsValid, Format(orderingValidation.Errors));
        AssertCanonicalTableOrderRejections(CustomLoopRunArtifactSerializer.Serialize(orderingRun), orderingRun);
        Assert.Equal(encoded.Length, new FileInfo(artifactPath).Length);
    }

    private async Task AssertPublicStoreReservationClassesAsync(MaximumExecutionFixture fixture)
    {
        var starts = fixture.Store.Transitions.Where(transition => Appended(transition).Any(IsAttemptStart)).ToArray();
        Assert.Equal(CustomLoopLimits.MaxModelAttemptsPerRun, starts.Length);
        Assert.Equal(55, starts.Count(transition => !IsExitStart(Appended(transition).Single(IsAttemptStart))));
        Assert.Equal(10, starts.Count(transition => IsExitStart(Appended(transition).Single(IsAttemptStart))));

        var distinct = new List<TraceTransition>();
        var repeated = new List<TraceTransition>();
        var seen = new HashSet<AttemptStartShape>();
        foreach (var transition in starts)
        {
            var start = Appended(transition).Single(IsAttemptStart);
            var shape = new AttemptStartShape(IsExitStart(start), start.StepId!);
            (seen.Add(shape) ? distinct : repeated).Add(transition);
        }

        Assert.Equal(CustomLoopLimits.MaxInferenceSteps + 1, distinct.Count);
        Assert.Equal(CustomLoopLimits.MaxModelAttemptsPerRun - distinct.Count, repeated.Count);
        using (var admissionWorkspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(admissionWorkspace.RootPath);
            var store = new CustomLoopRunStore(paths);
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(fixture.Initial)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(fixture.Admitted, fixture.Initial.LifecycleVersion)).Status);
            var quota = await store.GetTraceQuotaAsync();
            _output.WriteLine($"MAX_ARTIFACT_ADMITTED_ACCOUNTED_BYTES={quota.AccountedTraceUtf8Bytes}");
            Assert.Equal(CustomLoopLimits.MaxRunTraceUtf8Bytes, quota.AccountedTraceUtf8Bytes);
            Assert.Equal(1, quota.ActiveReservationCount);
        }

        var firstOverallDelta = await PersistTransitionAsync("FIRST_OVERALL_START", distinct[0]);
        Assert.InRange(firstOverallDelta, 1, CustomLoopLimits.MaxFirstAttemptStartEvidenceUtf8Bytes);
        var laterDistinctDeltas = new List<long>();
        for (var index = 1; index < distinct.Count; index++)
        {
            laterDistinctDeltas.Add(await PersistTransitionAsync($"FIRST_DISTINCT_START_{index + 1}", distinct[index]));
        }

        Assert.All(laterDistinctDeltas, delta => Assert.InRange(delta, 1, CustomLoopLimits.MaxFirstDistinctNodeAttemptStartEvidenceUtf8Bytes));
        var repeatedDelta = await PersistTransitionAsync("REPEATED_START", repeated[^1]);
        Assert.InRange(repeatedDelta, 1, CustomLoopLimits.MaxAttemptStartEvidenceUtf8Bytes);

        var outcome = fixture.Store.Transitions.Last(transition => Appended(transition).Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted));
        var outcomeDelta = await PersistTransitionAsync("ATTEMPT_OUTCOME", outcome);
        Assert.InRange(outcomeDelta, 1, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);

        var toolTransitions = fixture.Store.Transitions.Where(transition => Appended(transition).Any(item => item.ToolEvidence is not null)).Take(4).ToArray();
        Assert.Equal(4, toolTransitions.Length);
        for (var index = 0; index < toolTransitions.Length; index++)
        {
            var evidence = Appended(toolTransitions[index]).Single(item => item.ToolEvidence is not null).ToolEvidence!;
            var toolDelta = await PersistTransitionAsync($"TOOL_PHASE_{index + 1}", toolTransitions[index]);
            Assert.InRange(toolDelta, 1, ToolPhaseBudget(evidence));
        }

        var control = fixture.Store.Transitions.Single(transition => transition.Current.Status == CustomLoopRunStatus.Admitted && transition.Candidate.Status == CustomLoopRunStatus.Running);
        Assert.InRange(await PersistTransitionAsync("RUNNING_CONTROL", control), 1, CustomLoopLimits.MaxTraceControlEventUtf8Bytes);
        var terminal = fixture.Store.Transitions.Single(transition => !transition.Current.IsTerminal && transition.Candidate.IsTerminal);
        Assert.InRange(await PersistTransitionAsync("TERMINAL_CONTROL", terminal), 1, CustomLoopLimits.MaxTraceControlEventUtf8Bytes);
    }

    [Fact]
    public void Public_artifact_contract_rejects_invalid_empty_and_oversize_inputs()
    {
        var initial = InitialRun(MaximumDefinition(), Authority());
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Serialize(initial with { SchemaVersion = 99 }));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize([]));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(new byte[CustomLoopLimits.MaxRunTraceUtf8Bytes + 1]));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes("[]")));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes("\"text\"")));
        Assert.Throws<ArgumentNullException>(() => CustomLoopRunArtifactSerializer.Deserialize(null!));
    }

    private static async Task<MaximumExecutionFixture> GenerateMaximumExecutionAsync()
    {
        var definition = MaximumDefinition();
        var authority = Authority();
        var initial = InitialRun(definition, authority);
        var admitted = CompleteAdmissionAudit(initial);
        var store = new MaximumRunStore(admitted);
        var executor = new MaximumExecutor(store, authority);
        var publisher = new PublishedConversation();
        var runner = new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, publisher, new NullAuditLog(), new FixedAuthorityProvider(authority), new FixedTimeProvider());
        var admittedValidation = CustomLoopRunValidator.ValidateForDispatch(admitted);
        Assert.True(admittedValidation.IsValid, Format(admittedValidation.Errors));

        var execution = await runner.RunAsync(new CustomLoopOrderedRunRequest(admitted.Id, "web"));

        Assert.True(execution.Status == CustomLoopOrderedRunStatus.Completed, $"Execution ended after {executor.Requests.Count} provider requests as {execution.Status}: {execution.Detail} Failure={execution.Run?.FailureCode}: {execution.Run?.FailureDetail}");
        return new MaximumExecutionFixture(initial, admitted, store, executor, publisher, execution);
    }

    private async Task<long> PersistTransitionAsync(string label, TraceTransition transition)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var currentArtifact = CustomLoopRunArtifactSerializer.Serialize(transition.Current);
        var artifactPath = await WriteArtifactAsync(paths, transition.Current, currentArtifact);
        var store = new CustomLoopRunStore(paths);

        if (Appended(transition).Any(IsAttemptStart))
        {
            Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(transition.Candidate, transition.Current.LifecycleVersion));
        }

        var result = await store.UpdateAsync(transition.Candidate, transition.Current.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        Assert.NotNull(result.Run);
        Assert.Equal(transition.Candidate.Events.Length, result.Run.Events.Length);
        var delta = new FileInfo(artifactPath).Length - currentArtifact.Length;
        _output.WriteLine($"MAX_ARTIFACT_{label}_DELTA_BYTES={delta}");
        Assert.True(delta > 0, $"{label} must append durable artifact evidence.");
        return delta;
    }

    private static async Task<string> WriteArtifactAsync(WorkspacePaths paths, CustomLoopRunRecord run, byte[] artifact)
    {
        var directory = Path.Combine(paths.CustomLoopRunsPath, run.LoopId);
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(directory, run.Id + ".json");
        await File.WriteAllBytesAsync(artifactPath, artifact);
        return artifactPath;
    }

    private static CustomLoopRunEvent[] Appended(TraceTransition transition) => transition.Candidate.Events.Skip(transition.Current.Events.Length).ToArray();

    private static bool IsAttemptStart(CustomLoopRunEvent item) => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted;

    private static bool IsExitStart(CustomLoopRunEvent item) => item.Kind == CustomLoopRunEventKind.ExitDecisionStarted;

    private static int ToolPhaseBudget(CustomLoopToolTraceEvidence evidence)
    {
        return evidence.Phase switch
        {
            CustomLoopToolEvidencePhase.RequestReserved => CustomLoopLimits.MaxGovernedToolRequestEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.GovernanceDecided => CustomLoopLimits.MaxGovernedToolGovernanceEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved when !evidence.ReturnedToModel => CustomLoopLimits.MaxGovernedToolOutcomeEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
            _ => throw new InvalidOperationException($"Unsupported tool-evidence phase `{evidence.Phase}`.")
        };
    }

    private static CustomLoopDefinition MaximumDefinition()
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-maximum", "role-maximum", "step-1", "create-maximum", Now);
        var nodePolicy = new CustomLoopContextPolicy(
            new CustomLoopContextInputPolicy(true, true, true, true, true),
            new CustomLoopContextOutputPolicy(true, true));
        var steps = Enumerable.Range(1, CustomLoopLimits.MaxInferenceSteps)
            .Select(index => new CustomLoopInferenceStep($"step-{index}", MaxText($"name-{index}", CustomLoopLimits.MaxNameCharacters), MaxText($"instruction-{index}", CustomLoopLimits.MaxInstructionCharacters), CustomLoopNodeContextPolicy.Override(nodePolicy)))
            .ToArray();
        var definition = seed with
        {
            DisplayName = MaxText("display", CustomLoopLimits.MaxNameCharacters),
            Description = MaxText("description", CustomLoopLimits.MaxDescriptionCharacters),
            TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Preset, MaxText("preset", CustomLoopLimits.MaxPresetPromptCharacters), true),
            InferenceSteps = steps,
            ToolAssignments = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search],
            ExitPolicy = new CustomLoopExitPolicy(CustomLoopLimits.MaxAdditionalIterations, MaxText("exit", CustomLoopLimits.MaxInstructionCharacters), CustomLoopNodeContextPolicy.Override(nodePolicy))
        };
        return CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
    }

    private static CustomLoopRunRecord InitialRun(CustomLoopDefinition definition, CustomLoopToolAuthoritySnapshot authority)
    {
        var admitted = new CustomLoopRunEvent(1, "event-admitted", Now, CustomLoopRunEventKind.Admitted, null, null, null, "Maximum run admitted.", [], null, null, null, null, null, null, "provider", "model", null, null, authority);
        var context = MaximumContext();
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-maximum",
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            Now,
            Now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "invoke-maximum",
            "test-user",
            string.Empty,
            definition,
            definition.TriggerPolicy.PresetPrompt!,
            new CustomLoopConversationReference("conversation-maximum", CustomLoopTraceContentHash.Compute("conversation-version"), Now),
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted],
            null,
            null,
            null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord CompleteAdmissionAudit(CustomLoopRunRecord run)
    {
        var admissionAudit = new CustomLoopRunEvent(2, "event-admission-audit", Now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        return run with { LifecycleVersion = 2, Events = [.. run.Events, admissionAudit] };
    }

    private static CustomLoopContextSnapshot MaximumContext()
    {
        var workspace = new[]
        {
            ("nearest-agents", "root/AGENTS.md", CustomLoopContextSource.RoleInstruction, CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            ("agent", "root/.agent/AGENT.md", CustomLoopContextSource.RoleInstruction, CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            ("soul", "root/.agent/SOUL.md", CustomLoopContextSource.RoleInstruction, CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            ("personality", "root/.agent/PERSONALITY.md", CustomLoopContextSource.RoleInstruction, CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            ("context", "root/.agent/CONTEXT.md", CustomLoopContextSource.ContextualState, CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User),
            ("memory", "root/.agent/MEMORY.md", CustomLoopContextSource.ContextualState, CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User),
            ("models", "root/.agent/models.json", CustomLoopContextSource.ContextualState, CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User)
        };
        var sources = new List<CustomLoopContextManifestSource>();
        foreach (var (sourceId, sourcePath, sourceType, provenance, trust, role) in workspace)
        {
            var content = MaxText(sourceId, CustomLoopLimits.MaxInstructionCharacters);
            sources.Add(Source(sources.Count + 1, sourceType, sourceId, sourcePath, provenance, trust, role, content));
        }

        var baseCharacters = CustomLoopLimits.MaxInvokingConversationCharacters / CustomLoopLimits.MaxInvokingConversationEntries;
        var remainder = CustomLoopLimits.MaxInvokingConversationCharacters % CustomLoopLimits.MaxInvokingConversationEntries;
        for (var index = 0; index < CustomLoopLimits.MaxInvokingConversationEntries; index++)
        {
            var characters = baseCharacters + (index < remainder ? 1 : 0);
            var id = $"invoking-conversation-{index + 1:D3}";
            sources.Add(Source(sources.Count + 1, CustomLoopContextSource.InvokingConversation, id, $"conversation/maximum/messages/{index + 1}", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, MaxText(id, characters)));
        }

        var snapshot = new CustomLoopContextSnapshot(CustomLoopContextSnapshot.CurrentSchemaVersion, Now, sources.ToArray(), string.Empty);
        return CustomLoopContextSnapshotHash.Apply(snapshot);
    }

    private static CustomLoopContextManifestSource Source(int order, CustomLoopContextSource sourceType, string sourceId, string sourcePath, CustomLoopContextProvenance provenance, CustomLoopContextTrustClass trust, LlmMessageRole role, string content)
    {
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trust, role, content, CustomLoopTraceContentHash.Compute(content), content.Length, content.Length, false, null, null, Now);
    }

    private static CustomLoopToolAuthoritySnapshot Authority()
    {
        var assignments = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        return new CustomLoopToolAuthoritySnapshot(
            "role-maximum",
            assignments,
            assignments,
            assignments,
            assignments,
            CustomLoopTraceContentHash.Compute("role-maximum-list-read-search"),
            CustomLoopTraceContentHash.Compute("catalog-list-read-search"),
            Now,
            true,
            MaxText("authority", CustomLoopLimits.MaxToolGovernanceDetailCharacters));
    }

    private static string MaxText(string label, int length)
    {
        var prefix = label + ":\"\\\n";
        return prefix.Length >= length ? prefix[..length] : prefix + new string('\uE000', length - prefix.Length);
    }

    private static string Format(IReadOnlyList<CustomLoopValidationError> errors)
    {
        return string.Join(Environment.NewLine, errors.Select(error => $"{error.Code}:{error.Field}:{error.Message}"));
    }

    private static CustomLoopRunRecord WithSecondAuthority(CustomLoopRunRecord run)
    {
        var secondReservation = run.Events.Where(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.RequestReserved).Skip(1).First();
        var attemptStart = run.Events.Last(item => item.Sequence < secondReservation.Sequence
            && item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && item.Iteration == secondReservation.Iteration
            && string.Equals(item.StepId, secondReservation.StepId, StringComparison.Ordinal)
            && item.Attempt == secondReservation.Attempt);
        var alternate = attemptStart.ToolAuthority! with { Detail = MaxText("alternate-authority", CustomLoopLimits.MaxToolGovernanceDetailCharacters) };
        var events = run.Events.Select(item => item.Iteration == attemptStart.Iteration
                && string.Equals(item.StepId, attemptStart.StepId, StringComparison.Ordinal)
                && item.Attempt == attemptStart.Attempt
                && (item.Kind == CustomLoopRunEventKind.NodeAttemptStarted || item.ToolEvidence is not null)
            ? item with { ToolAuthority = alternate, ToolEvidence = item.ToolEvidence is null ? null : item.ToolEvidence with { Authority = alternate } }
            : item).ToArray();
        return run with { Events = events };
    }

    private static void AssertCanonicalTableOrderRejections(byte[] encoded, CustomLoopRunRecord expected)
    {
        var cases = new[]
        {
            (Table: "content", Reference: "$content"),
            (Table: "contextBlocks", Reference: "$contextBlock"),
            (Table: "authorities", Reference: "$authority"),
            (Table: "toolRequests", Reference: "$toolRequest")
        };
        foreach (var testCase in cases)
        {
            var mutated = SwapFirstTwoTableRowsAndReferences(encoded, testCase.Table, testCase.Reference);
            Assert.NotEqual(encoded, mutated);
            var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(mutated));
            Assert.Contains("not the one canonical hydrate-and-reproject encoding", exception.Message, StringComparison.Ordinal);
            var restored = SwapFirstTwoTableRowsAndReferences(mutated, testCase.Table, testCase.Reference);
            Assert.Equal(encoded, restored);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(CustomLoopRunArtifactSerializer.Deserialize(restored)));
        }
    }

    private static byte[] SwapFirstTwoTableRowsAndReferences(byte[] encoded, string tableName, string referenceProperty)
    {
        var root = JsonNode.Parse(encoded) as JsonObject ?? throw new InvalidOperationException("The canonical test envelope must be an object.");
        Assert.Equal(encoded, SerializeEnvelopeNode(root));
        var table = root[tableName]!.AsArray();
        Assert.True(table.Count >= 2, $"Canonical-order mutation requires two `{tableName}` rows.");
        var first = table[0]!.DeepClone().AsObject();
        var second = table[1]!.DeepClone().AsObject();
        var firstId = first["id"]!.GetValue<string>();
        var secondId = second["id"]!.GetValue<string>();
        second["id"] = firstId;
        first["id"] = secondId;
        table[0] = second;
        table[1] = first;
        SwapReferences(root, referenceProperty, firstId, secondId);
        RehashStructuralTables(root);
        return SerializeEnvelopeNode(root);
    }

    private static void SwapReferences(JsonNode? node, string referenceProperty, string firstId, string secondId)
    {
        if (node is JsonObject owner)
        {
            foreach (var property in owner.ToArray())
            {
                if (string.Equals(property.Key, referenceProperty, StringComparison.Ordinal) && property.Value is JsonValue value)
                {
                    var id = value.GetValue<string>();
                    owner[property.Key] = string.Equals(id, firstId, StringComparison.Ordinal) ? secondId : string.Equals(id, secondId, StringComparison.Ordinal) ? firstId : id;
                }
                else
                {
                    SwapReferences(property.Value, referenceProperty, firstId, secondId);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                SwapReferences(item, referenceProperty, firstId, secondId);
            }
        }
    }

    private static void RehashStructuralTables(JsonObject root)
    {
        foreach (var table in new[] { (Name: "contextBlocks", Value: "contextBlock"), (Name: "authorities", Value: "authority"), (Name: "toolRequests", Value: "toolRequest") })
        {
            foreach (var item in root[table.Name]!.AsArray())
            {
                var entry = item!.AsObject();
                var valueBytes = JsonSerializer.SerializeToUtf8Bytes(entry[table.Value]!, CanonicalJsonOptions);
                entry["sha256"] = Convert.ToHexString(SHA256.HashData(valueBytes)).ToLowerInvariant();
            }
        }
    }

    private static byte[] SerializeEnvelopeNode(JsonObject root)
    {
        return Encoding.UTF8.GetBytes(root.ToJsonString(CanonicalJsonOptions) + "\n");
    }

    private static void AssertCanonicalReferences(byte[] encoded)
    {
        var root = JsonNode.Parse(encoded)!.AsObject();
        var contentIds = root["content"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(root["content"]!.AsArray().Count, contentIds.Count);
        var references = new HashSet<string>(StringComparer.Ordinal);
        CollectContentReferences(root["contextBlocks"], references);
        CollectContentReferences(root["authorities"], references);
        CollectContentReferences(root["toolRequests"], references);
        CollectContentReferences(root["run"], references);
        Assert.True(contentIds.SetEquals(references));
        Assert.All(references, reference => Assert.Contains(reference, contentIds));
    }

    private void WriteEnvelopeByteBreakdown(byte[] encoded)
    {
        var root = JsonNode.Parse(encoded)!.AsObject();
        foreach (var property in root)
        {
            _output.WriteLine($"MAX_ARTIFACT_{property.Key.ToUpperInvariant()}_BYTES={Encoding.UTF8.GetByteCount(property.Value?.ToJsonString() ?? "null")}");
        }

        foreach (var property in root["run"]!.AsObject())
        {
            _output.WriteLine($"MAX_ARTIFACT_RUN_{property.Key.ToUpperInvariant()}_BYTES={Encoding.UTF8.GetByteCount(property.Value?.ToJsonString() ?? "null")}");
        }
    }

    private static void CollectContentReferences(JsonNode? node, HashSet<string> references)
    {
        if (node is JsonObject owner)
        {
            if (owner.Count == 1 && owner.TryGetPropertyValue("$content", out var reference))
            {
                references.Add(reference!.GetValue<string>());
                return;
            }

            foreach (var property in owner)
            {
                CollectContentReferences(property.Value, references);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectContentReferences(item, references);
            }
        }
    }

    private sealed class MaximumExecutor(ICustomLoopRunStore store, CustomLoopToolAuthoritySnapshot authority) : ICustomLoopInferenceAttemptExecutor
    {
        private int _inferenceCount;
        private int _exitCount;
        private int _toolCount;

        public List<CustomLoopInferenceAttemptRequest> Requests { get; } = [];

        public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        {
            Requests.Add(request);
            providerRequestStarted?.Invoke();
            if (request.IsExit)
            {
                _exitCount++;
                return new CustomLoopInferenceAttemptResult("Repeat", "provider", "model", $"exit-response-{_exitCount}");
            }

            _inferenceCount++;
            var consumed = 0;
            if (_toolCount < CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun)
            {
                _toolCount++;
                await AppendToolProtocolAsync(request, authority, _toolCount, _toolCount > CustomLoopLimits.MaxGovernedToolRequestsPerRun, cancellationToken);
                consumed = 1;
            }

            return new CustomLoopInferenceAttemptResult(MaxText($"inference-output-{_inferenceCount:D2}", CustomLoopLimits.MaxCanonicalModelOutputCharacters), "provider", "model", $"inference-response-{_inferenceCount}", consumed);
        }

        private async Task AppendToolProtocolAsync(CustomLoopInferenceAttemptRequest attempt, CustomLoopToolAuthoritySnapshot authority, int toolIndex, bool denied, CancellationToken cancellationToken)
        {
            var correlationId = $"tool-correlation-{toolIndex}";
            var brokerId = $"broker-request-{toolIndex}";
            var target = MaxText($"target-{toolIndex}", CustomLoopLimits.MaxGovernedToolTargetCharacters);
            var content = MaxText($"content-{toolIndex}", CustomLoopLimits.MaxGovernedToolArgumentCharacters);
            var pattern = MaxText($"pattern-{toolIndex}", CustomLoopLimits.MaxGovernedToolArgumentCharacters);
            var resolved = MaxText($"resolved-{toolIndex}", CustomLoopLimits.MaxGovernedToolTargetCharacters);
            var request = new ToolRequest(ToolCommand.Search, target, content, pattern, correlationId);
            var governance = denied
                ? new ToolGovernanceEvidence(
                    ToolAuthorityDecision.Denied,
                    MaxText($"governance-authority-{toolIndex}", CustomLoopLimits.MaxToolGovernanceDetailCharacters),
                    null,
                    null,
                    null,
                    null,
                    ToolApprovalDecision.NotEvaluated,
                    null,
                    null)
                : new ToolGovernanceEvidence(
                    ToolAuthorityDecision.Allowed,
                    MaxText($"governance-authority-{toolIndex}", CustomLoopLimits.MaxToolGovernanceDetailCharacters),
                    PermissionDecision.Allow,
                    MaxText($"permission-path-{toolIndex}", CustomLoopLimits.MaxGovernedToolTargetCharacters),
                    MaxText($"permission-detail-{toolIndex}", CustomLoopLimits.MaxToolGovernanceDetailCharacters),
                    CustomLoopTraceContentHash.Compute($"permission-policy-{toolIndex}"),
                    ToolApprovalDecision.NotRequired,
                    MaxText($"decision-by-{toolIndex}", CustomLoopLimits.MaxToolGovernanceDetailCharacters),
                    MaxText($"approval-detail-{toolIndex}", CustomLoopLimits.MaxToolGovernanceDetailCharacters));
            var outcomeValue = denied ? ToolExecutionOutcome.Denied : ToolExecutionOutcome.Succeeded;
            var formatted = ToolResultFormatter.FormatResults([new ToolResult(outcomeValue, MaxText($"tool-result-{toolIndex}", CustomLoopLimits.MaxCanonicalToolResultCharacters * 2), brokerId, resolved, request, governance)]);
            Assert.Equal(CustomLoopLimits.MaxCanonicalToolResultCharacters, formatted.Length);
            var hash = CustomLoopTraceContentHash.Compute(formatted);
            var reservation = Evidence(CustomLoopToolEvidencePhase.RequestReserved, null, null, null, false);
            var governed = Evidence(CustomLoopToolEvidencePhase.GovernanceDecided, brokerId, governance, null, false);
            var outcome = Evidence(CustomLoopToolEvidencePhase.OutcomeObserved, brokerId, governance, outcomeValue, false);
            var returned = outcome with { ReturnedToModel = true };
            var evidences = new[] { reservation, governed, outcome, returned };
            var kinds = new[] { CustomLoopRunEventKind.ToolRequestReserved, CustomLoopRunEventKind.ToolGovernanceDecided, CustomLoopRunEventKind.ToolOutcomeObserved, CustomLoopRunEventKind.ToolOutcomeObserved };
            for (var index = 0; index < evidences.Length; index++)
            {
                var current = await store.GetAsync(attempt.RunId, cancellationToken);
                Assert.NotNull(current);
                var toolEvent = new CustomLoopRunEvent(
                    current.Events.Length + 1,
                    $"event-tool-{toolIndex}-{index + 1}",
                    Now,
                    kinds[index],
                    attempt.Iteration,
                    attempt.StepId,
                    attempt.Attempt,
                    $"Tool evidence phase {index + 1} retained.",
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
                    null,
                    authority,
                    evidences[index]);
                var candidate = current with { LifecycleVersion = current.LifecycleVersion + 1, Events = [.. current.Events, toolEvent] };
                var updated = await store.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken);
                Assert.Equal(CustomLoopRunStoreStatus.Updated, updated.Status);
            }

            CustomLoopToolTraceEvidence Evidence(CustomLoopToolEvidencePhase phase, string? requestId, ToolGovernanceEvidence? toolGovernance, ToolExecutionOutcome? toolOutcome, bool returnedToModel)
            {
                var hasOutcome = phase == CustomLoopToolEvidencePhase.OutcomeObserved;
                return new CustomLoopToolTraceEvidence(
                    phase,
                    1,
                    correlationId,
                    requestId,
                    ToolCommand.Search,
                    target,
                    content,
                    pattern,
                    resolved,
                    authority,
                    toolGovernance,
                    toolOutcome,
                    hasOutcome ? formatted : null,
                    hasOutcome ? hash : null,
                    hasOutcome ? formatted.Length : null,
                    returnedToModel,
                    CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
            }
        }
    }

    private sealed class MaximumRunStore(CustomLoopRunRecord current) : ICustomLoopRunStore
    {
        public CustomLoopRunRecord Current { get; private set; } = current;
        public List<TraceTransition> Transitions { get; } = [];

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(string.Equals(Current.Id, runId, StringComparison.Ordinal) ? Current : null);

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            if (Current.LifecycleVersion != expectedLifecycleVersion)
            {
                return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
            }

            var validation = CustomLoopRunValidator.ValidateUpdate(Current, run);
            Assert.True(validation.IsValid, Format(validation.Errors));
            Transitions.Add(new TraceTransition(Current, run));
            Current = run;
            return Task.FromResult(CustomLoopRunStoreResult.Updated(run));
        }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(Current.IsTerminal ? null : Current);
        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>(Current.IsTerminal ? [] : [Current]);

        public Task<bool> HasSufficientTraceCapacityForDispatchAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Current.LifecycleVersion, expectedLifecycleVersion);
            return Task.FromResult(true);
        }
    }

    private sealed record MaximumExecutionFixture(
        CustomLoopRunRecord Initial,
        CustomLoopRunRecord Admitted,
        MaximumRunStore Store,
        MaximumExecutor Executor,
        PublishedConversation Publisher,
        CustomLoopOrderedRunResult Execution);

    private sealed record TraceTransition(CustomLoopRunRecord Current, CustomLoopRunRecord Candidate);

    private sealed record AttemptStartShape(bool IsExit, string StepId);

    private sealed class FixedAuthorityProvider(CustomLoopToolAuthoritySnapshot authority) : ICustomLoopToolAuthorityProvider
    {
        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(admittedMaximum.Count == 0 ? authority with { AdmittedMaximum = [], CurrentRoleCeiling = [], ImplementedCatalog = [], EffectiveAssignments = [] } : authority);
        }
    }

    private sealed class PublishedConversation : ICustomLoopConversationPublisher
    {
        public List<CustomLoopConversationPublicationRequest> Requests { get; } = [];

        public Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            request.AppendStarted?.Invoke();
            return Task.FromResult(new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Published, request.OperationId, "Published."));
        }
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
