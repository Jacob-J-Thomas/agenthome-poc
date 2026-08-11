using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

// Scenario methods stay centralized so the scheduling wrappers preserve the exact existing behavior.
// Every scenario owns its fixture and provider process; this type must not gain mutable shared state.
internal static class GovernedLoopRuntimeTests
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";

    internal static async Task Public_runtime_executes_exact_canonical_inputs_and_terminal_replay_precedes_workspace_busy_and_restart()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-canonical-success", "answer through the governed graph");

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var executed = await runtime.InvokeGovernedLoopAsync(input);

            Assert.True(string.Equals("Executed", executed.Status, StringComparison.Ordinal), executed.Detail);
            Assert.Equal("Admitted", executed.AdmissionStatus);
            Assert.Equal("Ready", executed.MaterializationStatus);
            Assert.True(
                string.Equals("Completed", executed.ExecutionStatus, StringComparison.Ordinal),
                $"{executed.Detail} Run failure: {executed.Run?.FailureCode}/{executed.Run?.FailureDetail}");
            Assert.True(executed.WasDispatched);
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), executed.Run?.Status);
            Assert.Equal(1, fixture.ProviderAttempts);

            using var runStore = new CustomLoopRunStore(fixture.Paths);
            var durableRun = Assert.IsType<CustomLoopRunRecord>(
                await runStore.GetAsync(executed.Run!.Id));
            Assert.NotNull(durableRun.SequentialAdapterBinding);
            Assert.NotNull(durableRun.SequentialInvocationSnapshot);
            Assert.Equal(input.OperationId, durableRun.SequentialAdapterBinding!.AdmissionOperationId);
            AssertFrontierProjection(
                Assert.IsType<LoopRunFrontierSnapshot>(executed.Run.Frontier),
                Assert.IsType<GovernedLoopFrontierPosture>(durableRun.Frontier));

            await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
            using var competingLease = Assert.IsAssignableFrom<IDisposable>(
                competingGate.TryAcquire("competing-terminal-replay", Hash64('8')).Lease);
            var terminalReplay = await runtime.InvokeGovernedLoopAsync(input);

            Assert.Equal("Terminal", terminalReplay.Status);
            Assert.Equal("Replayed", terminalReplay.AdmissionStatus);
            Assert.False(terminalReplay.WasDispatched);
            Assert.Equal(executed.Run.Id, terminalReplay.Run?.Id);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using (var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true))
        {
            var replay = await restarted.InvokeGovernedLoopAsync(input);

            Assert.Equal("Terminal", replay.Status);
            Assert.Equal("Replayed", replay.AdmissionStatus);
            Assert.False(replay.WasDispatched);
            Assert.Equal(1, fixture.ProviderAttempts);

            var defaultTurn = await restarted.RunTurnAsync("legacy default path remains selected");
            Assert.True(defaultTurn.IsMessageTurn);
            Assert.Contains("legacy default path remains selected", defaultTurn.Output, StringComparison.Ordinal);
        }
    }

    internal static async Task First_bound_completion_replays_after_restart_and_rejects_a_second_run_without_provider_dispatch()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var firstInput = fixture.Input("invoke-first-bound-success", "complete the first bound run");

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var completed = await runtime.InvokeGovernedLoopAsync(firstInput);
            var replayed = await runtime.InvokeGovernedLoopAsync(firstInput);

            Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
            Assert.Equal("Completed", completed.ExecutionStatus);
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
            Assert.True(completed.WasDispatched);
            Assert.Equal("Terminal", replayed.Status);
            Assert.Equal("Replayed", replayed.AdmissionStatus);
            Assert.Equal("Completed", replayed.ExecutionStatus);
            Assert.False(replayed.WasDispatched);
            Assert.Equal(completed.Run?.Id, replayed.Run?.Id);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var restartReplay = await restarted.InvokeGovernedLoopAsync(firstInput);
        var rejected = await restarted.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-second", "a second run must not dispatch"));

        Assert.Equal("Terminal", restartReplay.Status);
        Assert.Equal("Replayed", restartReplay.AdmissionStatus);
        Assert.Equal("Completed", restartReplay.ExecutionStatus);
        Assert.False(restartReplay.WasDispatched);
        Assert.True(string.Equals("Executed", rejected.Status, StringComparison.Ordinal), rejected.Detail);
        Assert.Equal("Failed", rejected.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), rejected.Run?.Status);
        Assert.Equal("effect_authority_denied", rejected.Run?.FailureCode);
        Assert.False(rejected.WasDispatched);
        Assert.Equal(1, fixture.ProviderAttempts);
    }

    internal static async Task Failed_provider_attempt_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion,
            failFirstAttempts: 1);
        await using var runtime = await fixture.CreateRuntimeAsync();

        var failed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-provider-failure", "fail before completion"));
        var completed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-provider-failure", "complete after the failed attempt"));

        Assert.Equal("Failed", failed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), failed.Run?.Status);
        Assert.True(failed.WasDispatched);
        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.True(completed.WasDispatched);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Paused_then_cancelled_run_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var invocation = runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-pause-cancel", "pause and cancel before completion"));
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);

        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, "pause-first-bound-before-cancel"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var paused = await invocation;
        Assert.Equal("Paused", paused.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), paused.Run?.Status);
        var cancelled = await runtime.CancelCustomLoopAsync(
            new LoopRunControlInput(paused.Run!.Id, paused.Run.LifecycleVersion, "cancel-first-bound-paused-run"));
        var completed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-cancel", "complete after cancellation"));

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled.ToString(), cancelled.Run?.Status);
        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Publication_conflict_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var invocation = runtime.InvokeGovernedLoopAsync(
                fixture.Input("invoke-first-bound-publication-conflict", "conflict before publication"));
            await fixture.WaitForProviderAsync();
            await new ConversationMemoryStore(fixture.Paths).AppendMessageAsync(
                LlmMessage.User("interleaving durable conversation message"));
            fixture.ReleaseProvider();

            var failed = await invocation;
            Assert.Equal("Failed", failed.ExecutionStatus);
            Assert.Equal(CustomLoopRunStatus.Failed.ToString(), failed.Run?.Status);
            Assert.Equal("conversation_publication_failed", failed.Run?.FailureCode);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var completed = await restarted.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-publication-conflict", "complete after publication conflict"));

        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Definitive_authority_rejection_replays_after_restart_without_materialization_or_provider_work()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(includeRestrictedGrant: true);
        var input = fixture.Input("invoke-canonical-rejected", "must be rejected", fixture.RestrictedGrant!);

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var rejected = await runtime.InvokeGovernedLoopAsync(input);
            var replayed = await runtime.InvokeGovernedLoopAsync(input);

            Assert.True(string.Equals("Rejected", rejected.Status, StringComparison.Ordinal), rejected.Detail);
            Assert.Equal("Rejected", rejected.AdmissionStatus);
            Assert.NotNull(rejected.AdmissionFailureCode);
            Assert.Null(rejected.MaterializationStatus);
            Assert.Null(rejected.Run);
            Assert.False(rejected.WasDispatched);
            Assert.Equal("Rejected", replayed.Status);
            Assert.Equal("Replayed", replayed.AdmissionStatus);
            Assert.False(replayed.WasDispatched);
            Assert.Equal(0, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var restartReplay = await restarted.InvokeGovernedLoopAsync(input);

        Assert.Equal("Rejected", restartReplay.Status);
        Assert.Equal("Replayed", restartReplay.AdmissionStatus);
        Assert.False(restartReplay.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Begin_before_snapshot_bind_recovers_only_when_the_exact_snapshot_can_be_reproduced()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-crash-window-exact", "recover the exact prepared snapshot");
        await using var runtime = await fixture.CreateRuntimeAsync();
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-before-bind", Hash64('7'));
        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, competing.Status);

        using (competing.Lease)
        {
            var interrupted = await runtime.InvokeGovernedLoopAsync(input);

            Assert.Equal("WorkspaceBusy", interrupted.Status);
            Assert.False(interrupted.WasDispatched);
            Assert.Equal(0, fixture.ProviderAttempts);
            var unbound = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
                await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));
            Assert.Equal(CustomLoopInvocationBindingState.Unbound, unbound.BindingState);
            Assert.Null(unbound.SequentialInvocationSnapshot);
        }

        var recovered = await runtime.InvokeGovernedLoopAsync(input);
        var durable = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
            await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));

        Assert.True(string.Equals("Executed", recovered.Status, StringComparison.Ordinal), recovered.Detail);
        Assert.True(recovered.WasDispatched);
        Assert.Equal(1, fixture.ProviderAttempts);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, durable.BindingState);
        Assert.NotNull(durable.SequentialInvocationSnapshot);
        Assert.Equal(durable.CreatedAtUtc, durable.SequentialInvocationSnapshot!.ContextCapturedAtUtc);
    }

    internal static async Task Begin_before_snapshot_bind_conflicts_when_context_changes_and_does_zero_provider_work()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-crash-window-changed", "do not substitute changed context");
        await using var runtime = await fixture.CreateRuntimeAsync();
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-before-context-change", Hash64('6'));

        using (Assert.IsAssignableFrom<IDisposable>(competing.Lease))
        {
            var interrupted = await runtime.InvokeGovernedLoopAsync(input);
            Assert.Equal("WorkspaceBusy", interrupted.Status);
        }

        await File.AppendAllTextAsync(
            fixture.Paths.AgentFile("CONTEXT.md"),
            $"{Environment.NewLine}changed after durable Begin{Environment.NewLine}");
        var conflicted = await runtime.InvokeGovernedLoopAsync(input);
        var durable = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
            await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));

        Assert.Equal("Conflict", conflicted.Status);
        Assert.False(conflicted.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
        Assert.Equal(CustomLoopInvocationBindingState.Unbound, durable.BindingState);
        Assert.Null(durable.SequentialInvocationSnapshot);
    }

    internal static async Task Public_pause_and_resume_reconstructs_the_canonical_plan_from_durable_evidence()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(pauseProvider: true, inferenceSteps: 2);
        var input = fixture.Input("invoke-canonical-pause", "pause-at-boundary");
        await using var runtime = await fixture.CreateRuntimeAsync();

        var invocation = runtime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        Assert.NotNull(running.SequentialAdapterBinding);
        Assert.NotNull(running.SequentialInvocationSnapshot);

        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, "pause-canonical-run"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var pausedInvocation = await invocation;
        Assert.Equal("Paused", pausedInvocation.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), pausedInvocation.Run?.Status);
        var paused = Assert.IsType<CustomLoopRunRecord>(
            await runStore.GetAsync(running.Id));
        var resume = await runtime.ResumeCustomLoopAsync(
            new LoopRunControlInput(paused.Id, paused.LifecycleVersion, "resume-canonical-run"));

        Assert.Equal("Completed", resume.Status);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), resume.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
        Assert.Equal(paused.SequentialAdapterBinding?.ContentHash, (await runStore.GetAsync(paused.Id))?.SequentialAdapterBinding?.ContentHash);
    }

    internal static async Task Public_resume_revalidates_the_exact_grant_before_the_next_provider_effect(
        AuthorityGrantOperationKind transition)
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            inferenceSteps: 2,
            grantLifetime: transition == AuthorityGrantOperationKind.Expire ? TimeSpan.FromSeconds(8) : null);
        var token = transition.ToString().ToLowerInvariant();
        var input = fixture.Input($"invoke-before-{token}", $"stop after exact {token}");
        await using var runtime = await fixture.CreateRuntimeAsync();

        var invocation = runtime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, $"pause-before-{token}"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var pausedInvocation = await invocation;
        Assert.Equal("Paused", pausedInvocation.ExecutionStatus);
        Assert.Equal(1, fixture.ProviderAttempts);
        var paused = Assert.IsType<CustomLoopRunRecord>(await runStore.GetAsync(running.Id));
        await fixture.TransitionGrantAsync(transition);

        var resume = await runtime.ResumeCustomLoopAsync(
            new LoopRunControlInput(paused.Id, paused.LifecycleVersion, $"resume-after-{token}"));

        Assert.Equal("Failed", resume.Status);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), resume.Run?.Status);
        Assert.True(
            resume.Detail.Contains("governed effect", StringComparison.OrdinalIgnoreCase)
                || resume.Detail.Contains("before dispatch", StringComparison.OrdinalIgnoreCase),
            resume.Detail);
        Assert.Equal(1, fixture.ProviderAttempts);
        await AssertNoToolExecutionAsync(fixture.Paths);
    }

    internal static async Task Public_absent_exact_grant_is_rejected_without_provider_or_tool_effects()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var missing = fixture.MissingGrantReference();
        await using var runtime = await fixture.CreateRuntimeAsync();

        var rejected = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-absent-grant", "the absent grant must not dispatch", missing));

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Rejected", rejected.AdmissionStatus);
        Assert.False(rejected.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
        await AssertNoToolExecutionAsync(fixture.Paths);
    }

    private static async Task<CustomLoopRunRecord> WaitForRunAsync(
        CustomLoopRunStore store,
        CustomLoopRunStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = (await store.ListNonterminalAsync()).SingleOrDefault(item => item.Status == status);
            if (run is not null)
            {
                return run;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"A canonical run did not reach {status} within the test deadline.");
    }

    private static async Task AssertNoToolExecutionAsync(WorkspacePaths paths)
    {
        var events = await new AuditLog(paths).ReadTailAsync(1_000);
        Assert.DoesNotContain(events, item => item.Action == AuditSchema.Actions.ToolExecute);
    }

    private static void AssertFrontierProjection(
        LoopRunFrontierSnapshot snapshot,
        GovernedLoopFrontierPosture frontier)
    {
        Assert.Equal(frontier.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(frontier.WorkspaceId, snapshot.WorkspaceId);
        Assert.Equal(frontier.Binding.SchemaVersion, snapshot.Binding.SchemaVersion);
        Assert.Equal(frontier.Binding.RunId, snapshot.Binding.RunId);
        Assert.Equal(frontier.Binding.Revision.GraphId, snapshot.Binding.GraphId);
        Assert.Equal(frontier.Binding.Revision.RevisionId, snapshot.Binding.RevisionId);
        Assert.Equal(frontier.Binding.Revision.ExecutableHash, snapshot.Binding.ExecutableHash);
        Assert.Equal(frontier.Binding.ExecutionGeneration, snapshot.Binding.ExecutionGeneration);
        Assert.Equal(frontier.GraphArtifactHash, snapshot.GraphArtifactHash);
        Assert.Equal(frontier.GraphLayoutHash, snapshot.GraphLayoutHash);
        Assert.Equal(frontier.AdmissionReceiptHash, snapshot.AdmissionReceiptHash);
        Assert.Equal(frontier.Payload.FrontierVersion, snapshot.FrontierVersion);
        Assert.Equal(frontier.Payload.ConcurrencyCeiling, snapshot.ConcurrencyCeiling);
        Assert.Equal(frontier.Payload.Status.ToString(), snapshot.Status);
        Assert.Equal(frontier.Payload.UpdatedAtUtc, snapshot.UpdatedAtUtc);
        Assert.Equal(frontier.Payload.ContentHash, snapshot.ContentHash);
        Assert.Equal(frontier.Payload.Nodes.Count, snapshot.Nodes.Count);
        foreach (var (expected, actual) in frontier.Payload.Nodes.Zip(snapshot.Nodes))
        {
            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.PlanOrdinal, actual.PlanOrdinal);
            Assert.Equal(expected.NodeId, actual.NodeId);
            Assert.Equal(expected.Descriptor.Kind.ToString(), actual.Kind);
            Assert.Equal(expected.Descriptor.TypeId, actual.TypeId);
            Assert.Equal(expected.Descriptor.Version, actual.DescriptorVersion);
            Assert.Equal(expected.IncomingControlEdgeIds, actual.IncomingControlEdgeIds);
            Assert.Equal(expected.OutgoingControlEdgeIds, actual.OutgoingControlEdgeIds);
            Assert.Equal(expected.Status.ToString(), actual.Status);
            Assert.Equal(expected.Attempt, actual.Attempt);
            Assert.Equal(expected.AttemptOperationId, actual.AttemptOperationId);
            Assert.Equal(expected.OutcomeEvidenceId, actual.OutcomeEvidenceId);
            Assert.Equal(expected.OutcomeEvidenceHash, actual.OutcomeEvidenceHash);
        }
    }

    private static string Hash64(char value) => new(value, 64);

    private sealed class GovernedRuntimeFixture : IDisposable
    {
        private const string OwnerActorId = "governed-test-owner";
        private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly TestWorkspace _workspace;
        private readonly string _providerCounterPath;
        private readonly string _providerStartedPath;
        private readonly string _providerReleasePath;
        private readonly string _codexPath;

        private GovernedRuntimeFixture(
            TestWorkspace workspace,
            GovernedLoopRevisionPublicationPin publication,
            AuthorityGrantReference grant,
            AuthorityGrantReference? restrictedGrant,
            string codexPath)
        {
            _workspace = workspace;
            Publication = publication;
            Grant = grant;
            RestrictedGrant = restrictedGrant;
            _codexPath = codexPath;
            Paths = new WorkspacePaths(workspace.RootPath);
            _providerCounterPath = workspace.File("governed-provider-attempts.txt");
            _providerStartedPath = workspace.File("governed-provider-started.marker");
            _providerReleasePath = workspace.File("governed-provider-release.marker");
        }

        public WorkspacePaths Paths { get; }

        public GovernedLoopRevisionPublicationPin Publication { get; }

        public AuthorityGrantReference Grant { get; }

        public AuthorityGrantReference? RestrictedGrant { get; }

        public int ProviderAttempts
            => File.Exists(_providerCounterPath)
                ? int.Parse(File.ReadAllText(_providerCounterPath), System.Globalization.CultureInfo.InvariantCulture)
                : 0;

        public static async Task<GovernedRuntimeFixture> CreateAsync(
            bool includeRestrictedGrant = false,
            bool pauseProvider = false,
            int inferenceSteps = 1,
            TimeSpan? grantLifetime = null,
            AuthorityGrantCompletionConstraintKind completionConstraint = AuthorityGrantCompletionConstraintKind.None,
            int failFirstAttempts = 0)
        {
            Assert.InRange(inferenceSteps, 1, 2);
            Assert.InRange(failFirstAttempts, 0, 2);
            var workspace = new TestWorkspace();
            try
            {
                await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
                var paths = new WorkspacePaths(workspace.RootPath);
                var role = await CreateRoleAsync(paths);
                var publication = await CreatePublishedGraphAsync(workspace, paths, role, inferenceSteps);
                var grant = await CreateGrantAsync(
                    workspace,
                    paths,
                    role,
                    publication,
                    "governed-full-grant",
                    FullCeiling(),
                    grantLifetime,
                    completionConstraint);
                var restricted = includeRestrictedGrant
                    ? await CreateGrantAsync(
                        workspace,
                        paths,
                        role,
                        publication,
                        "governed-empty-grant",
                        EmptyCeiling(),
                        null,
                        AuthorityGrantCompletionConstraintKind.None)
                    : null;
                var codexPath = await CreateCodexExecutableAsync(workspace, pauseProvider, failFirstAttempts);
                return new GovernedRuntimeFixture(workspace, publication, grant, restricted, codexPath);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public GovernedLoopRunInvocationInput Input(
            string operationId,
            string prompt,
            AuthorityGrantReference? grant = null)
            => new(operationId, Publication, grant ?? Grant, prompt);

        public Task<AgentRuntime> CreateRuntimeAsync(bool preserveCurrentConversation = false)
            => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                    new RejectingApprovalPrompt(),
                    _workspace.ServerStatePath,
                    CompatibleRuntimeStatus())
                .CreateAsync(
                    "test-model",
                    _workspace.RootPath,
                    _codexPath,
                    "read-only",
                    AgentRuntimeSurface.Cli,
                    preserveCurrentConversation);

        public async Task WaitForProviderAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(_providerStartedPath) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(File.Exists(_providerStartedPath), "The governed provider attempt did not start within the test deadline.");
        }

        public void ReleaseProvider() => File.WriteAllText(_providerReleasePath, "release");

        public AuthorityGrantReference MissingGrantReference()
            => new(GrantId("governed-absent-grant"), GrantRevision(1), "sha256:" + Hash64('f'));

        public async Task TransitionGrantAsync(AuthorityGrantOperationKind kind)
        {
            Assert.Contains(
                kind,
                new[]
                {
                    AuthorityGrantOperationKind.Narrow,
                    AuthorityGrantOperationKind.Suspend,
                    AuthorityGrantOperationKind.Replace,
                    AuthorityGrantOperationKind.Revoke,
                    AuthorityGrantOperationKind.Expire,
                });
            var trust = new FileCapabilityCatalogTrustProvider(_workspace.ServerStatePath);
            var store = new AuthorityProfileStore(Paths, trust);
            var read = await store.ReadAsync(Grant.GrantId);
            Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
            var current = Assert.IsType<AuthorityGrant>(read.Snapshot?.CurrentGrant);
            var status = kind switch
            {
                AuthorityGrantOperationKind.Suspend => AuthorityGrantLifecycleStatus.Suspended,
                AuthorityGrantOperationKind.Revoke => AuthorityGrantLifecycleStatus.Revoked,
                AuthorityGrantOperationKind.Expire => AuthorityGrantLifecycleStatus.Expired,
                _ => AuthorityGrantLifecycleStatus.Active,
            };
            var ceiling = kind == AuthorityGrantOperationKind.Narrow
                ? new AuthorityCeiling(
                    current.RequestedCeiling.Capabilities
                        .Where(item => item.Id.Value != ModelInferenceCapabilityId)
                        .ToArray(),
                    current.RequestedCeiling.DataClasses,
                    current.RequestedCeiling.MaxTargetCount,
                    current.RequestedCeiling.MaxSideEffectClass,
                    current.RequestedCeiling.AllowsRecurrence,
                    current.RequestedCeiling.AllowsExternalPublication,
                    current.RequestedCeiling.AllowsIrreversibleAction)
                : current.RequestedCeiling;
            var recordedAtUtc = kind == AuthorityGrantOperationKind.Expire
                ? current.Boundary.ExpiresAtUtc!.Value
                : DateTimeOffset.UtcNow;
            var successor = AuthorityGrantHash.Apply(current with
            {
                Revision = GrantRevision(current.Revision.Value + 1),
                PredecessorRevision = current.Revision,
                PredecessorContentHash = current.ContentHash,
                Status = status,
                RequestedCeiling = ceiling,
                RecordedAtUtc = recordedAtUtc,
                ContentHash = string.Empty,
            });
            var operationId = $"transition-{kind.ToString().ToLowerInvariant()}";
            var requestHash = Hash64('b');
            var observed = await store.ReadForMutationAsync(current.GrantId, operationId, requestHash);
            var evidence = new AuthorityGrantOperationEvidence(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                operationId,
                requestHash,
                kind,
                AuthorityGrantOperationOutcome.Committed,
                AuthorityGrantOperationFailureCode.None,
                successor.GrantId,
                current.Revision.Value,
                new AuthorityGrantReference(successor.GrantId, successor.Revision, successor.ContentHash),
                successor.ChangedByActorId,
                successor.Reason,
                Hash64('c'),
                kind is AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace ? Hash64('d') : null,
                successor.RecordedAtUtc);
            var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, successor, evidence));
            Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        }

        public void Dispose() => _workspace.Dispose();

        private CodexRuntimeStatus CompatibleRuntimeStatus()
            => new(
                CodexRuntimeCompatibility.Compatible,
                _codexPath,
                _codexPath,
                "codex-cli compatible-governed-test",
                "test-model",
                "controlled test",
                "The exact fake app-server and provider path remains exercised; redundant compatibility probes are covered separately.");

        private static async Task<ContextualRoleRevision> CreateRoleAsync(WorkspacePaths paths)
        {
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
                ContextualRoleLimits.SchemaVersion,
                new ContextualRoleRevisionIdentity("governed-helper", 1),
                string.Empty,
                "Governed helper",
                "Execute one bounded canonical inference graph.",
                ContextualRoleStatus.Published,
                new ContextualRoleProvenance(OwnerActorId, _now.AddMinutes(-2), _now.AddMinutes(-1)),
                new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
                new ContextualRoleInstructionSourceReference(
                    ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                    "role",
                    ContextualRoleInstructionClassification.RoleInstruction),
                new ContextualRolePolicyMaxima(
                    ImmutableArray.Create(ConversationTurnCapabilityId, ModelInferenceCapabilityId))));
            var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
                "create-governed-helper-role",
                string.Empty,
                ContextualRoleRevisionMutationKind.Create,
                revision.Identity.RoleId,
                OwnerActorId,
                revision,
                null,
                _now));
            using var store = new ContextualRoleRevisionStore(paths, workspaceId);
            var result = await store.MutateAsync(request);
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, result.Status);
            return Assert.IsType<ContextualRoleRevision>(result.Revision);
        }

        private static async Task<GovernedLoopRevisionPublicationPin> CreatePublishedGraphAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            ContextualRoleRevision role,
            int inferenceSteps)
        {
            var candidate = Candidate(new ContextualRoleRevisionPin(role.Identity, role.ContentHash), inferenceSteps);
            var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
            Assert.True(normalized.IsValid);
            var revision = normalized.Graph!.RevisionReference;
            var lifecycle = new ContextualRoleLifecycleSnapshot(
                1,
                role.Identity.RoleId,
                role.Identity,
                ContextualRoleLifecycleState.Active,
                "create-governed-helper-role",
                ContextualRoleRevisionMutationKind.Create,
                _now);
            var authority = new StaticAuthorityProvider(new GovernedLoopAuthoritySnapshot(
                true,
                Hash64('a'),
                new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
                role,
                lifecycle,
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                ContextualRoleInstructionSourceProbeStatus.Ready,
                role.PolicyMaxima.CapabilityIds,
                CustomLoopLimits.MaxGraphNodeAttempts,
                100_000,
                CustomLoopLimits.MaxGraphNodeEvidenceItems,
                100));
            var service = GovernedLoopGraphAuthoringFactory.Create(
                paths,
                new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
                new StaticNodeCatalog(Catalog(candidate)),
                authority,
                new AllowingActorAuthorizer());
            var created = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(
                1,
                new GovernedLoopRevisionLifecycleRequest(
                    1,
                    "create-governed-sequential-graph",
                    GovernedLoopRevisionOperationKind.CreateDraft,
                    revision.GraphId,
                    Actor(),
                    GovernedLoopRevisionLifecycleStatus.Unknown,
                    0,
                    null,
                    null,
                    revision,
                    null,
                    null),
                candidate));
            Assert.True(
                created.Status == GovernedLoopGraphAuthoringStatus.Committed,
                string.Join(Environment.NewLine, created.GraphValidationErrors.Select(error => $"{error.Code}: {error.Message}")));
            var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(created.LifecycleResult?.Head);
            var published = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(
                1,
                new GovernedLoopRevisionLifecycleRequest(
                    1,
                    "publish-governed-sequential-graph",
                    GovernedLoopRevisionOperationKind.Publish,
                    head.GraphId,
                    Actor(),
                    head.Status,
                    head.LifecycleVersion,
                    head.DraftRevision,
                    head.PublishedRevision,
                    null,
                    head.DraftRevision,
                    null),
                null));
            Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, published.Status);
            return Assert.IsType<GovernedLoopRevisionPublicationPin>(published.LifecycleResult?.Head?.PublishedRevision);
        }

        private static async Task<AuthorityGrantReference> CreateGrantAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            ContextualRoleRevision role,
            GovernedLoopRevisionPublicationPin publication,
            string grantId,
            AuthorityCeiling requestedCeiling,
            TimeSpan? grantLifetime,
            AuthorityGrantCompletionConstraintKind completionConstraint)
        {
            var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
            var store = new AuthorityProfileStore(paths, trust);
            var profileId = ProfileId("governed-loop-profile");
            var profileRead = await store.ReadAsync(profileId.Value);
            AuthorityProfileRecord profile;
            if (profileRead.Status == AuthorityProfileReadStatus.NotFound)
            {
                var declaration = new AuthorityProfile(
                    AuthorityProfile.CurrentSchemaVersion,
                    profileId,
                    ProfileRevision(1),
                    AuthorityProfileStatus.Active,
                    Purpose("Bound canonical sequential test execution."),
                    new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration),
                    _now.AddMinutes(-5),
                    _now.AddDays(1),
                    FullCeiling(),
                    []);
                var created = await store.MutateAsync(new AuthorityProfileMutation(
                    AuthorityProfileMutationKind.Create,
                    "create-governed-loop-profile",
                    0,
                    declaration,
                    null,
                    null,
                    Actor(),
                    Purpose("Create the bounded governed-loop authority profile.")));
                Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
                profile = Assert.IsType<AuthorityProfileRecord>(created.Record);
            }
            else
            {
                Assert.Equal(AuthorityProfileReadStatus.Available, profileRead.Status);
                profile = Assert.IsType<AuthorityProfileRecord>(profileRead.Record);
            }

            var binding = new AuthorityGrantBinding(
                new AuthorityGrantProfilePin(
                    new AuthorityProfileReference(profile.ProfileId, profile.CurrentProfile.Revision),
                    profile.CurrentHash),
                new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
                publication);
            var recordedAtUtc = DateTimeOffset.UtcNow;
            var grant = AuthorityGrantHash.Apply(new AuthorityGrant(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                GrantId(grantId),
                GrantRevision(1),
                null,
                null,
                AuthorityGrantLifecycleStatus.Active,
                binding,
                requestedCeiling,
                new AuthorityGrantBoundary(
                    recordedAtUtc.AddMinutes(-1),
                    recordedAtUtc.Add(grantLifetime ?? TimeSpan.FromHours(12)),
                    completionConstraint),
                Actor(),
                Purpose("Delegate one exact published governed loop."),
                recordedAtUtc,
                string.Empty));
            var operationId = "create-" + grantId;
            var requestHash = grantId.EndsWith("empty-grant", StringComparison.Ordinal) ? Hash64('3') : Hash64('2');
            var observed = await store.ReadForMutationAsync(grant.GrantId, operationId, requestHash);
            var evidence = new AuthorityGrantOperationEvidence(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                operationId,
                requestHash,
                AuthorityGrantOperationKind.Create,
                AuthorityGrantOperationOutcome.Committed,
                AuthorityGrantOperationFailureCode.None,
                grant.GrantId,
                0,
                new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
                grant.ChangedByActorId,
                grant.Reason,
                Hash64('4'),
                Hash64('5'),
                grant.RecordedAtUtc);
            var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, grant, evidence));
            Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
            var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
            if (requestedCeiling.Capabilities.Count != 0)
            {
                await AssertDependenciesActiveAsync(workspace, paths, binding, reference);
            }

            return reference;
        }

        private static async Task AssertDependenciesActiveAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            AuthorityGrantBinding binding,
            AuthorityGrantReference reference)
        {
            var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
            var transaction = new CapabilityAuthorityTransaction(paths);
            var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
            var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
            var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
            var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
            using var roleStore = new ContextualRoleRevisionStore(
                paths,
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                authorityTransaction: transaction);
            var roleSource = new AuthorityGrantRoleSource(
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                roleStore,
                roleStore,
                new WorkspaceContextualRoleInstructionSourceProbe(paths),
                transaction);
            var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
            var profileSource = new AuthorityGrantProfileSource(authorityStore);
            var resolver = new AuthorityGrantResolver(
                authorityStore,
                profileSource,
                roleSource,
                publicationSource,
                bindingSource,
                transaction);
            var bindingResult = await bindingSource.ResolveAsync(binding.Loop);
            var roleResult = await roleSource.ResolveAsync(binding.Role);
            var profileRead = await authorityStore.ReadAsync(binding.Profile.Reference.ProfileId.Value);
            var profileEvaluatedAtUtc = DateTimeOffset.UtcNow;
            var profileResult = await profileSource.ResolveAsync(binding.Profile, profileEvaluatedAtUtc);
            var grantResult = await resolver.ResolveAsync(reference);

            Assert.Equal(AuthorityGrantDependencyStatus.Active, bindingResult.Status);
            Assert.Equal(AuthorityGrantDependencyStatus.Active, roleResult.Status);
            Assert.Equal(AuthorityProfileReadStatus.Available, profileRead.Status);
            Assert.Equal(AuthorityGrantDependencyStatus.Active, profileResult.Status);
            Assert.Equal(AuthorityGrantResolutionStatus.Active, grantResult.Status);
        }

        private static GovernedLoopGraphCandidate Candidate(ContextualRoleRevisionPin role, int inferenceSteps)
        {
            var nodes = new List<GovernedLoopNodeDefinition>
            {
                new(
                    "trigger",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                    [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
            };
            var controlEdges = new List<GovernedLoopControlEdgeDefinition>();
            var bindings = new List<GovernedLoopBindingDefinition>();
            var display = new List<GovernedLoopNodeDisplayMetadata>
            {
                new("trigger", "Trigger", "Start.", 0, 0),
            };
            var dataSourceNodeId = "trigger";
            var dataSourcePortId = "request";
            for (var index = 1; index <= inferenceSteps; index++)
            {
                var nodeId = index == 1 ? "inference" : $"inference-{index}";
                nodes.Add(new GovernedLoopNodeDefinition(
                    nodeId,
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
                    [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                    GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
                    new Dictionary<string, string> { ["instruction"] = $"Answer bounded inference step {index}." }));
                controlEdges.Add(new GovernedLoopControlEdgeDefinition(
                    index == 1 ? "trigger-to-inference" : $"inference-{index - 1}-to-inference-{index}",
                    dataSourceNodeId,
                    nodeId,
                    index == 1 ? GovernedLoopControlCondition.Always : GovernedLoopControlCondition.Success));
                bindings.Add(new GovernedLoopBindingDefinition($"request-binding-{index}", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, nodeId, "request"));
                bindings.Add(new GovernedLoopBindingDefinition($"context-binding-{index}", GovernedLoopBindingKind.Context, "trigger", "invocation-context", nodeId, "invocation-context"));
                display.Add(new GovernedLoopNodeDisplayMetadata(nodeId, $"Inference {index}", "Infer.", index * 100, 0));
                dataSourceNodeId = nodeId;
                dataSourcePortId = "result";
            }

            nodes.Add(new GovernedLoopNodeDefinition(
                "exit",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()));
            controlEdges.Add(new GovernedLoopControlEdgeDefinition("inference-to-exit", dataSourceNodeId, "exit", GovernedLoopControlCondition.Success));
            bindings.Add(new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, "exit", "result"));
            display.Add(new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", (inferenceSteps + 1) * 100, 0));

            return new GovernedLoopGraphCandidate(
                1,
                "governed-sequential-loop",
                "revision-1",
                "Execute one canonical sequential inference chain.",
                role,
                "trigger",
                ["exit"],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId]),
                [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
                nodes,
                controlEdges,
                bindings,
                new GovernedLoopOutputContract(
                    "Return the bounded result.",
                    [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
                new GovernedLoopDisplayMetadata("Governed sequential loop", "Public Startup composition test.", display));
        }

        private static GovernedLoopPortDefinition Port(
            string id,
            GovernedLoopPortDirection direction,
            GovernedLoopBindingKind kind)
            => new(id, direction, kind, "text", true);

        private static GovernedLoopNodeCatalogSnapshot Catalog(GovernedLoopGraphCandidate candidate)
        {
            var schemas = candidate.ValueSchemas!.ToDictionary(schema => schema!.Id, schema => schema!.Kind, StringComparer.Ordinal);
            var terminal = candidate.TerminalNodeIds!.ToHashSet(StringComparer.Ordinal);
            var descriptors = candidate.Nodes!.DistinctBy(node => node!.Descriptor).Select(node =>
            {
                var outcomes = candidate.ControlEdges!
                    .Where(edge => string.Equals(edge!.FromNodeId, node!.Id, StringComparison.Ordinal))
                    .Select(edge => edge!.Condition)
                    .Distinct()
                    .Order()
                    .ToArray();
                return new GovernedLoopNodeCatalogDescriptor(
                    node!.Descriptor,
                    true,
                    true,
                    node.Descriptor.Kind == GovernedLoopNodeKind.Trigger,
                    terminal.Contains(node.Id),
                    outcomes,
                    outcomes,
                    GovernedLoopJoinPolicy.None,
                    0,
                    false,
                    null,
                    null,
                    node.Ports.Select(port => new GovernedLoopCatalogPortContract(
                        port.Id,
                        port.Direction,
                        port.BindingKind,
                        schemas[port.ValueSchemaId],
                        port.Required)).ToArray(),
                    node.Parameters.Select(parameter => new GovernedLoopCatalogParameterContract(
                        parameter.Key,
                        GovernedLoopParameterValueKind.Text,
                        true,
                        1,
                        CustomLoopLimits.MaxGraphParameterValueCharacters,
                        null,
                        null,
                        [])).ToArray(),
                    node.AuthorityCeiling.CapabilityIds,
                    new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
            }).ToArray();
            return new GovernedLoopNodeCatalogSnapshot(true, "governed-runtime-catalog", descriptors);
        }

        private static AuthorityCeiling FullCeiling()
            => new(
                BuiltInCapabilityCatalog.Descriptors
                    .Where(item => item.Id.Value is ConversationTurnCapabilityId or ModelInferenceCapabilityId)
                    .Select(CreateCapabilityIdentity)
                    .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
                [],
                1,
                CapabilitySideEffectClass.None,
                false,
                true,
                false);

        private static AuthorityCeiling EmptyCeiling()
            => new([], [], 0, CapabilitySideEffectClass.None, false, false, false);

        private static CapabilityDescriptorIdentity CreateCapabilityIdentity(CapabilityDescriptor descriptor)
        {
            Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation));
            Assert.True(validation.IsValid);
            return identity!;
        }

        private static AuthorityActorId Actor()
        {
            Assert.True(AuthorityActorId.TryParse(OwnerActorId, out var actor, out _));
            return actor!;
        }

        private static AuthorityPurpose Purpose(string value)
        {
            Assert.True(AuthorityPurpose.TryParse(value, out var purpose, out _));
            return purpose!;
        }

        private static AuthorityProfileId ProfileId(string value)
        {
            Assert.True(AuthorityProfileId.TryParse(value, out var id, out _));
            return id!;
        }

        private static AuthorityProfileRevision ProfileRevision(int value)
        {
            Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
            return revision!;
        }

        private static AuthorityGrantId GrantId(string value)
        {
            Assert.True(AuthorityGrantId.TryParse(value, out var id, out _));
            return id!;
        }

        private static AuthorityGrantRevision GrantRevision(int value)
        {
            Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
            return revision!;
        }

        private static async Task<string> CreateCodexExecutableAsync(
            TestWorkspace workspace,
            bool pauseProvider,
            int failFirstAttempts)
        {
            var scriptPath = workspace.File("fake-governed-codex.js");
            var commandPath = workspace.File(OperatingSystem.IsWindows() ? "fake-governed-codex.cmd" : "fake-governed-codex");
            var counterPath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-attempts.txt"));
            var startedPath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-started.marker"));
            var releasePath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-release.marker"));
            var pauseEveryTurn = pauseProvider ? "true" : "false";
            await File.WriteAllTextAsync(scriptPath, $$"""
                const fs = require("node:fs");
                const readline = require("node:readline");
                const counterPath = {{counterPath}};
                const startedPath = {{startedPath}};
                const releasePath = {{releasePath}};
                const pauseEveryTurn = {{pauseEveryTurn}};
                const failFirstAttempts = {{failFirstAttempts}};

                if (process.argv.slice(2).includes("--version")) {
                  process.stdout.write("codex-cli compatible-governed-test\n");
                  process.exit(0);
                }

                const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
                let threadNumber = 0;
                let turnNumber = 0;

                function write(value) {
                  process.stdout.write(`${JSON.stringify(value)}\n`);
                }

                function complete(threadId, turnId, text) {
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
                  write({
                    method: "turn/completed",
                    params: { threadId, turnId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text }] } }
                  });
                }

                function prompt(message) {
                  const text = (message.params?.input ?? []).map(item => String(item?.text ?? "")).join("\n");
                  const marker = "Current user message:";
                  const index = text.indexOf(marker);
                  return index < 0 ? text : text.slice(index + marker.length).trim();
                }

                function waitForRelease(callback) {
                  if (fs.existsSync(releasePath)) {
                    callback();
                    return;
                  }

                  setTimeout(() => waitForRelease(callback), 20);
                }

                input.on("line", line => {
                  const message = JSON.parse(line);
                  switch (message.method) {
                    case "initialize":
                      write({ id: message.id, result: {} });
                      break;
                    case "model/list":
                      write({ id: message.id, result: { data: [{ id: "test-model", model: "test-model" }], nextCursor: null } });
                      break;
                    case "thread/start": {
                      const threadId = `thread-governed-${++threadNumber}`;
                      write({ id: message.id, result: { thread: { id: threadId } } });
                      break;
                    }
                    case "turn/start": {
                      const threadId = String(message.params?.threadId ?? `thread-governed-${threadNumber}`);
                      const turnId = `turn-governed-${++turnNumber}`;
                      const userPrompt = prompt(message);
                      const attempts = fs.existsSync(counterPath) ? Number(fs.readFileSync(counterPath, "utf8")) : 0;
                      const attemptNumber = attempts + 1;
                      fs.writeFileSync(counterPath, String(attemptNumber));
                      write({ id: message.id, result: { turn: { id: turnId } } });
                      if (attemptNumber <= failFirstAttempts) {
                        write({
                          method: "turn/completed",
                          params: { threadId, turnId, turn: { id: turnId, status: "failed", error: { message: "planned governed provider failure" }, items: [] } }
                        });
                        break;
                      }
                      const finish = () => complete(threadId, turnId, `governed response: ${userPrompt}`);
                      if (pauseEveryTurn && !fs.existsSync(releasePath)) {
                        fs.writeFileSync(startedPath, "started");
                        waitForRelease(finish);
                      } else {
                        finish();
                      }
                      break;
                    }
                    default:
                      break;
                  }
                });
                """);
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(commandPath, """
                    @echo off
                    node "%~dp0fake-governed-codex.js" %*
                    """);
            }
            else
            {
                var escaped = scriptPath
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("$", "\\$", StringComparison.Ordinal)
                    .Replace("`", "\\`", StringComparison.Ordinal);
                await File.WriteAllTextAsync(commandPath, $"#!/bin/sh\nexec node \"{escaped}\" \"$@\"\n");
                File.SetUnixFileMode(
                    commandPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return commandPath;
        }
    }

    private sealed class StaticNodeCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class StaticAuthorityProvider(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(
            ContextualRoleRevisionPin? owningRole,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class AllowingActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                GovernedLoopRevisionActorAuthorizationStatus.Authorized,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                Hash64('9')));
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(
            AgentToolApprovalRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult((false, "test", "No governed workspace tool is used in this graph."));
    }
}
