using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class HumanInputConversationCommandAdapterTests
{
    [Fact]
    public async Task Response_commands_require_the_complete_inspected_state_before_accepting_private_payloads()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await HumanInputConversationTestRuntime.CreateAsync(workspace);
        const string PrivateValue = "private-unbound-value";

        var submit = await runtime.RunTurnAsync($"/human-input submit request-one operation-one response-one {{\"value\":{{\"kind\":\"text\",\"text\":\"{PrivateValue}\"}}}}");
        var withdraw = await runtime.RunTurnAsync("/human-input withdraw request-one operation-two response-one");
        var select = await runtime.RunTurnAsync("/human-input select request-one operation-three response-one");

        Assert.All([submit, withdraw, select], result =>
        {
            Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, result.Status);
            Assert.Contains("<lifecycle-version> <lifecycle-status> <request-version-id> <request-hash>", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(PrivateValue, result.Output, StringComparison.Ordinal);
        });
        Assert.Empty(runtime.GetActiveConversationTranscript());
    }

    [Fact]
    public async Task Inspect_and_submit_project_every_bounded_schema_without_exposing_authority_or_private_values()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = Store(workspace);
        var cases = SchemaCases();
        for (var index = 0; index < cases.Count; index++)
        {
            var item = cases[index];
            var mutation = HumanInputConversationTestRuntime.CreateRequest(
                workspace.RootPath,
                item.RequestId,
                $"version-{item.RequestId}",
                $"create-{item.RequestId}",
                index,
                item.Schema,
                item.Policy,
                $"Purpose for {item.RequestId}.",
                $"Prompt for {item.RequestId}.");
            Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);
        }

        await using var runtime = await HumanInputConversationTestRuntime.CreateAsync(workspace);
        for (var index = 0; index < cases.Count; index++)
        {
            var item = cases[index];
            var read = await runtime.HumanInput.ReadAsync(item.RequestId);
            var posture = Assert.IsType<HumanInputRequestPosture>(read.Request);
            var inspected = await runtime.RunTurnAsync($"/human-input inspect {item.RequestId}");
            var submitted = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
                Head(posture),
                $"submit-{item.RequestId}",
                $"response-{item.RequestId}",
                item.Payload));

            Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, inspected.Status);
            Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, submitted.Status);
            Assert.Contains($"Purpose: \"Purpose for {item.RequestId}.\"", inspected.Output, StringComparison.Ordinal);
            Assert.Contains($"Prompt: \"Prompt for {item.RequestId}.\"", inspected.Output, StringComparison.Ordinal);
            Assert.Contains(posture.Presentation.RequestHash, inspected.Output, StringComparison.Ordinal);
            Assert.Contains(HumanInputConversationTestRuntime.Terms(Head(posture)), inspected.Output, StringComparison.Ordinal);
            Assert.Contains(item.InspectionFragments[0], inspected.Output, StringComparison.Ordinal);
            Assert.All(item.InspectionFragments, fragment => Assert.Contains(fragment, inspected.Output, StringComparison.Ordinal));
            Assert.Contains("committed", submitted.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(item.PrivateValue, inspected.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(item.PrivateValue, submitted.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkspaceActors.Cli, inspected.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("cli-respondent", inspected.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("route-cli", inspected.Output, StringComparison.Ordinal);
        }

        Assert.Empty(runtime.GetActiveConversationTranscript());
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("withdraw")]
    [InlineData("select")]
    public async Task Response_commands_reject_the_exact_inspected_state_after_an_amendment(string command)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = Store(workspace);
        var policy = command == "submit"
            ? new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null)
            : new HumanInputResponsePolicy(HumanInputResponsePolicyKind.ManualSelection, null, ImmutableArray.Create("cli-respondent"));
        var pending = HumanInputConversationTestRuntime.CreateRequest(
            workspace.RootPath,
            $"request-stale-{command}",
            $"version-stale-{command}",
            $"create-stale-{command}",
            0,
            TextSchema(),
            policy);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(pending)).Status);
        await using var runtime = await HumanInputConversationTestRuntime.CreateAsync(workspace);
        var before = Assert.IsType<HumanInputRequestPosture>((await runtime.HumanInput.ReadAsync($"request-stale-{command}")).Request);
        if (command != "submit")
        {
            var seeded = await runtime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
                $"seed-{command}",
                HumanInputResponseOperationKind.Submit,
                before.RequestId,
                before.LifecycleVersion,
                before.Status,
                before.CurrentRequest,
                $"response-stale-{command}",
                new HumanInputResponseValue(HumanInputResponseKind.Text, "seed-private-value", null, null, null, null),
                null));
            Assert.Equal(HumanInputOperationStatus.Committed, seeded.Status);
        }

        var inspected = await runtime.RunTurnAsync($"/human-input inspect {before.RequestId}");
        var currentRead = await runtime.HumanInput.ReadAsync(before.RequestId);
        var current = Assert.IsType<HumanInputRequestPosture>(currentRead.Request);
        var currentRequest = Assert.IsType<HumanInputRequest>(pending.RequestToAppend);
        var currentHead = Assert.IsType<HumanInputRequestLifecycleHead>(pending.PrimaryHeadToWrite);
        var amended = HumanInputRequestStoreTestData.TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            currentRequest,
            currentHead,
            currentRead.StoreGeneration,
            $"amend-stale-{command}",
            HumanInputRequestStoreTestData.HashB);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(amended)).Status);
        var exactHead = Head(current);
        var mutationCommand = command == "submit"
            ? HumanInputConversationTestRuntime.SubmitCommand(exactHead, "stale-operation", "response-stale-submit", "{\"value\":{\"kind\":\"text\",\"text\":\"private-stale-value\"}}")
            : HumanInputConversationTestRuntime.TargetCommand(command, exactHead, "stale-operation", $"response-stale-{command}");

        var result = await runtime.RunTurnAsync(mutationCommand);
        var after = Assert.IsType<HumanInputRequestPosture>((await runtime.HumanInput.ReadAsync(before.RequestId)).Request);

        Assert.Contains(HumanInputConversationTestRuntime.Terms(exactHead), inspected.Output, StringComparison.Ordinal);
        Assert.Contains("conflict", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, after.LifecycleVersion);
        Assert.False(after.IsAnswered);
        Assert.DoesNotContain("private-stale-value", result.Output, StringComparison.Ordinal);
        Assert.Empty(runtime.GetActiveConversationTranscript());
    }

    [Fact]
    public async Task Exact_submit_replays_after_restart_and_rejects_changed_payload_for_the_same_operation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = Store(workspace);
        var pending = HumanInputConversationTestRuntime.CreateRequest(
            workspace.RootPath,
            "request-restart-exact",
            "version-restart-exact",
            "create-restart-exact",
            0,
            TextSchema());
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(pending)).Status);
        var head = Assert.IsType<HumanInputRequestLifecycleHead>(pending.PrimaryHeadToWrite);
        const string PrivateValue = "private-restart-exact";
        const string ChangedValue = "private-restart-changed";
        var command = HumanInputConversationTestRuntime.SubmitCommand(
            head,
            "submit-restart-exact",
            "response-restart-exact",
            $"{{\"value\":{{\"kind\":\"text\",\"text\":\"{PrivateValue}\"}}}}");

        await using (var initial = await HumanInputConversationTestRuntime.CreateAsync(workspace))
        {
            Assert.Contains("committed", (await initial.RunTurnAsync(command)).Output, StringComparison.OrdinalIgnoreCase);
        }

        await using var restarted = await HumanInputConversationTestRuntime.CreateAsync(workspace);
        var replayed = await restarted.RunTurnAsync(command);
        var changed = await restarted.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
            head,
            "submit-restart-exact",
            "response-restart-exact",
            $"{{\"value\":{{\"kind\":\"text\",\"text\":\"{ChangedValue}\"}}}}"));

        Assert.Contains("replayed", replayed.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict", changed.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateValue, replayed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ChangedValue, changed.Output, StringComparison.Ordinal);
        Assert.Empty(restarted.GetActiveConversationTranscript());
    }

    [Fact]
    public async Task Terminal_cache_entries_are_bounded_and_old_durable_operations_keep_replay_and_conflict_semantics()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = Store(workspace);
        var first = HumanInputConversationTestRuntime.CreateRequest(
            workspace.RootPath,
            "request-cache-first",
            "version-cache-first",
            "create-cache-first",
            0,
            TextSchema());
        var final = HumanInputConversationTestRuntime.CreateRequest(
            workspace.RootPath,
            "request-cache-final",
            "version-cache-final",
            "create-cache-final",
            1,
            TextSchema());
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(first)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(final)).Status);
        var firstHead = Assert.IsType<HumanInputRequestLifecycleHead>(first.PrimaryHeadToWrite);
        var finalHead = Assert.IsType<HumanInputRequestLifecycleHead>(final.PrimaryHeadToWrite);

        await using var runtime = await HumanInputConversationTestRuntime.CreateAsync(workspace);
        var firstCommitted = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
            firstHead,
            "submit-cache-first",
            "response-cache-first",
            "{\"value\":{\"kind\":\"text\",\"text\":\"x\"}}"));
        Assert.Contains("committed", firstCommitted.Output, StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < 255; index++)
        {
            var suffix = index.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            var requestId = $"request-cache-missing-{suffix}";
            var missingHead = new HumanInputRequestLifecycleHead(
                1,
                requestId,
                1,
                HumanInputRequestLifecycleStatus.Pending,
                new HumanInputRequestReference(1, requestId, $"version-cache-missing-{suffix}", HumanInputRequestStoreTestData.HashA),
                0,
                null,
                null,
                $"create-cache-missing-{suffix}",
                DateTimeOffset.UtcNow);
            var result = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
                missingHead,
                $"submit-cache-missing-{suffix}",
                $"response-cache-missing-{suffix}",
                "{\"value\":{\"kind\":\"text\",\"text\":\"x\"}}"));
            Assert.Contains("notfound", result.Output, StringComparison.OrdinalIgnoreCase);
        }

        var finalCommitted = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
            finalHead,
            "submit-cache-final",
            "response-cache-final",
            "{\"value\":{\"kind\":\"text\",\"text\":\"x\"}}"));
        var firstReplay = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
            firstHead,
            "submit-cache-first",
            "response-cache-first",
            "{\"value\":{\"kind\":\"text\",\"text\":\"x\"}}"));
        var firstConflict = await runtime.RunTurnAsync(HumanInputConversationTestRuntime.SubmitCommand(
            firstHead,
            "submit-cache-first",
            "response-cache-first",
            "{\"value\":{\"kind\":\"text\",\"text\":\"y\"}}"));

        Assert.Contains("committed", finalCommitted.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replayed", firstReplay.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict", firstConflict.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtime.GetActiveConversationTranscript());
    }

    private static HumanInputRequestStore Store(TestWorkspace workspace)
        => new(new WorkspacePaths(workspace.RootPath), new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));

    private static HumanInputRequestLifecycleHead Head(HumanInputRequestPosture posture)
        => new(
            1,
            posture.RequestId,
            posture.LifecycleVersion,
            posture.Status,
            posture.CurrentRequest,
            posture.ReminderCount,
            posture.SupersedesRequestId,
            posture.SupersededByRequestId,
            "projection-only",
            posture.UpdatedAtUtc);

    private static HumanInputResponseSchema TextSchema() => new(HumanInputResponseKind.Text, 128, null, null, null);

    private static IReadOnlyList<SchemaCase> SchemaCases()
        =>
        [
            new(
                "request-schema-text",
                TextSchema(),
                null,
                "{\"value\":{\"kind\":\"text\",\"text\":\"private-text\"}}",
                "private-text",
                ["Response schema: Text; maximum characters: 128", "Required response count: not applicable"]),
            new(
                "request-schema-choice",
                new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, [new HumanInputChoice("yes", "Yes, continue"), new HumanInputChoice("no", "No, stop")], null, null),
                new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Quorum, 2, null),
                "{\"value\":{\"kind\":\"choice\",\"choiceId\":\"yes\"}}",
                "private-choice-never-rendered",
                ["Response schema: Choice; choices: 2", "Choice `yes`: \"Yes, continue\"", "Choice `no`: \"No, stop\"", "Required response count: 2"]),
            new(
                "request-schema-confirmation",
                new HumanInputResponseSchema(HumanInputResponseKind.Confirmation, null, null, null, null),
                null,
                "{\"value\":{\"kind\":\"confirmation\",\"confirmation\":true}}",
                "private-confirmation-never-rendered",
                ["Response schema: Confirmation; value: true or false"]),
            new(
                "request-schema-structured",
                new HumanInputResponseSchema(
                    HumanInputResponseKind.Structured,
                    null,
                    null,
                    [
                        new HumanInputStructuredFieldSchema("note", HumanInputStructuredFieldKind.Text, true, 64, null),
                        new HumanInputStructuredFieldSchema("decision", HumanInputStructuredFieldKind.Choice, false, null, [new HumanInputChoice("continue", "Continue safely"), new HumanInputChoice("stop", "Stop safely")])
                    ],
                    null),
                null,
                "{\"value\":{\"kind\":\"structured\",\"fields\":[{\"fieldId\":\"note\",\"text\":\"private-structured\"},{\"fieldId\":\"decision\",\"choiceId\":\"continue\"}]}}",
                "private-structured",
                [
                    "Response schema: Structured; fields: 2",
                    "Field `note`: Text; required: yes; maximum characters: 64",
                    "Field `decision`: Choice; required: no; maximum characters: not applicable",
                    "Field `decision` choice `continue`: \"Continue safely\"",
                    "Field `decision` choice `stop`: \"Stop safely\""
                ]),
            new(
                "request-schema-reference",
                new HumanInputResponseSchema(HumanInputResponseKind.Reference, null, null, null, new HumanInputReferencePolicy(HumanInputReferenceKind.Artifact, 96)),
                null,
                "{\"value\":{\"kind\":\"reference\",\"reference\":{\"kind\":\"artifact\",\"value\":\"private-artifact-reference\"}}}",
                "private-artifact-reference",
                ["Response schema: Reference; kind: Artifact; maximum characters: 96"])
        ];

    private sealed record SchemaCase(
        string RequestId,
        HumanInputResponseSchema Schema,
        HumanInputResponsePolicy? Policy,
        string Payload,
        string PrivateValue,
        string[] InspectionFragments);
}
