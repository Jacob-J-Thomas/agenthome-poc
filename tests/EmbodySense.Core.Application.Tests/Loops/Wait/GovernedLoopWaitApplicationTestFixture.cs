using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Wait;

internal static class GovernedLoopWaitApplicationTestFixture
{
    internal static readonly DateTimeOffset Now = GovernedLoopSequentialApplicationTestFixture.Now;

    internal static WaitContext CreateTimestampContext(DateTimeOffset? deadlineUtc = null)
    {
        var deadline = deadlineUtc ?? Now.AddMinutes(5);
        return CreateContext(
            GovernedLoopSequentialNodeDescriptors.TimestampWait,
            GovernedLoopWaitVocabulary.DeadlineUtcParameter,
            deadline.ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
            deadline,
            null);
    }

    internal static WaitContext CreateParallelTimestampContext(DateTimeOffset? deadlineUtc = null)
    {
        var deadline = deadlineUtc ?? Now.AddMinutes(5);
        var parameterValue = deadline.ToString(
            GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);
        return CreateContext(ParallelArtifact(parameterValue), deadline, null);
    }

    internal static WaitContext CreateEventContext(string eventReference = "governed-event-1")
        => CreateContext(
            GovernedLoopSequentialNodeDescriptors.AuthenticatedEventWait,
            GovernedLoopWaitVocabulary.EventReferenceParameter,
            eventReference,
            null,
            eventReference);

    private static WaitContext CreateContext(
        GovernedLoopNodeDescriptor descriptor,
        string parameterId,
        string parameterValue,
        DateTimeOffset? deadlineUtc,
        string? eventReference)
    {
        var artifact = Artifact(descriptor, parameterId, parameterValue);
        return CreateContext(artifact, deadlineUtc, eventReference);
    }

    private static WaitContext CreateContext(
        GovernedLoopGraphRevisionArtifact artifact,
        DateTimeOffset? deadlineUtc,
        string? eventReference)
    {
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var revision = artifact.RevisionArtifact.Revision;
        var execution = GovernedLoopExecutionBinding.Create(1, "wait-run", revision, 1);
        var contextSnapshot = CustomLoopContextSnapshot.CreateEmpty(Now.AddMinutes(-2));
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Wait until the admitted instant.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            contextSnapshot.CapturedAtUtc,
            contextSnapshot.SourceManifest,
            string.Empty));
        var grant = SeedGrant(artifact, execution);
        var publication = grant.Intent.Publication;
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            grant.Intent.OperationId,
            invocation.ContentHash,
            string.Empty,
            publication,
            grant.Intent.AuthorityGrant,
            grant.Intent.ActorId,
            grant.Intent.Surface));
        var receipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(
            artifact,
            execution,
            grant.Intent.WorkspaceId,
            request.OperationId,
            request.RequestHash,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            receipt.Intent.WorkspaceId,
            execution,
            request.OperationId,
            receipt,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(GovernedLoopSequentialRunAnchorGuard.Create(binding, request, receipt, invocation, artifact).Anchor);
        var initialized = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Initialize(binding, plan, "trigger-attempt", Hash('1'), Hash('2'), Now.AddMinutes(-1)).Frontier);
        var selected = GovernedLoopSequentialFrontierMachine.Select(initialized, binding, plan);
        var started = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            initialized,
            binding,
            plan,
            selected.Node,
            selected.Activation,
            1,
            "wait-attempt-1",
            Now).Frontier);
        var activation = started.Payload.Nodes[selected.Activation!.ActivationOrdinal];
        var dispatch = new GovernedLoopSequentialNodeDispatchRequest(1, anchor, plan, selected.Node!, activation, 1);
        return new WaitContext(artifact, plan, binding, publication, started, dispatch, deadlineUtc, eventReference);
    }

    internal static GovernedLoopSleepCurrentPosture SleepPosture(
        WaitContext context,
        GovernedLoopFrontierPosture frontier,
        DateTimeOffset? observedAtUtc = null,
        GovernedLoopRunStatus? lifecycleStatus = null,
        bool unattendedExecutionPermitted = true,
        DateTimeOffset? executionExpiresAtUtc = null,
        string? postureHash = null)
    {
        var observedAt = observedAtUtc ?? frontier.Payload.UpdatedAtUtc;
        var runStatus = lifecycleStatus ?? (frontier.Payload.Status == GovernedLoopFrontierStatus.Waiting
            ? GovernedLoopRunStatus.Waiting
            : GovernedLoopRunStatus.Running);
        var lifecycle = GovernedLoopRunLifecycle.Create(
            context.Binding.ExecutionBinding,
            GovernedLoopRunLifecyclePayload.Create(
                1,
                2,
                runStatus,
                Now.AddHours(-1),
                frontier.Payload.UpdatedAtUtc,
                null));
        return new GovernedLoopSleepCurrentPosture(
            GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [], []),
            context.Publication,
            unattendedExecutionPermitted,
            Hash('3'),
            executionExpiresAtUtc,
            observedAt,
            postureHash ?? Hash('4'));
    }

    private static GovernedLoopGraphRevisionArtifact Artifact(
        GovernedLoopNodeDescriptor descriptor,
        string parameterId,
        string parameterValue)
    {
        var trigger = GovernedLoopSequentialApplicationTestFixture.Trigger("trigger");
        var wait = new GovernedLoopNodeDefinition(
            "wait",
            descriptor,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [parameterId] = parameterValue,
            });
        var exit = GovernedLoopSequentialApplicationTestFixture.Exit("exit");
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [trigger, wait, exit],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-wait", "trigger", "wait", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("wait-to-exit", "wait", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            bindings: [new GovernedLoopBindingDefinition("wait-to-exit-result", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact ParallelArtifact(string deadlineUtc)
    {
        var trigger = GovernedLoopSequentialApplicationTestFixture.Trigger("trigger");
        var waitA = Wait("wait-a", deadlineUtc);
        var waitB = Wait("wait-b", deadlineUtc);
        var join = new GovernedLoopNodeDefinition(
            "join",
            GovernedLoopSequentialNodeDescriptors.AllJoin,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal));
        var exit = GovernedLoopSequentialApplicationTestFixture.Exit("exit");
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [trigger, waitA, waitB, join, exit],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-wait-a", "trigger", "wait-a", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("trigger-to-wait-b", "trigger", "wait-b", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("wait-a-to-join", "wait-a", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("wait-b-to-join", "wait-b", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            bindings: [new GovernedLoopBindingDefinition("trigger-to-exit-result", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopNodeDefinition Wait(string nodeId, string deadlineUtc)
        => new(
            nodeId,
            GovernedLoopSequentialNodeDescriptors.TimestampWait,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = deadlineUtc,
            });

    private static EmbodySense.Core.Common.Loops.Admission.Models.GovernedLoopAdmissionReceipt SeedGrant(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopExecutionBinding execution)
        => GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(
            artifact,
            execution,
            "workspace-sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "admit-wait",
            Hash('5'),
            artifact.ArtifactHash,
            artifact.LayoutHash);

    internal static string Hash(char value) => new(value, 64);
}

internal sealed record WaitContext(
    GovernedLoopGraphRevisionArtifact Artifact,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopSequentialAdapterBinding Binding,
    EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopRevisionPublicationPin Publication,
    GovernedLoopFrontierPosture RunningFrontier,
    GovernedLoopSequentialNodeDispatchRequest DispatchRequest,
    DateTimeOffset? DeadlineUtc,
    string? EventReference);
