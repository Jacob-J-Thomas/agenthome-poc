using System.Text;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class GovernedLoopSchedulePayloadSourceTests
{
    [Fact]
    public async Task Invalid_references_and_constructor_arguments_fail_closed()
    {
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSchedulePayloadSource(null!, null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSchedulePayloadSource(new ScriptedScheduleStore(), null!));

        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        Assert.Equal("payload/daily-reflection", GovernedLoopSchedulePayloadSource.CreateReference(scheduleId!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSchedulePayloadSource.CreateReference(null!));

        var store = new ScriptedScheduleStore();
        var graphStore = new ScriptedGovernedLoopGraphRevisionStore();
        var source = new GovernedLoopSchedulePayloadSource(store, graphStore);
        foreach (var reference in new[] { null, string.Empty, "payload", "Payload/daily-reflection", "payload/not-a-schedule" })
        {
            var resolution = await source.ResolveAsync(reference!);
            Assert.Equal(ScheduleGovernedPayloadResolutionStatus.NotFound, resolution.Status);
            Assert.Null(resolution.ContentHash);
        }
    }

    [Fact]
    public async Task Schedule_store_outcomes_and_exceptions_are_mapped_without_graph_reads()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore();
        var graphStore = new ScriptedGovernedLoopGraphRevisionStore();
        var source = new GovernedLoopSchedulePayloadSource(store, graphStore);
        var reference = context.Definition.Payload.GovernedReference;

        foreach (var (status, expected) in new[]
        {
            (ScheduleStoreReadStatus.NotFound, ScheduleGovernedPayloadResolutionStatus.NotFound),
            (ScheduleStoreReadStatus.Backpressured, ScheduleGovernedPayloadResolutionStatus.Backpressured),
            (ScheduleStoreReadStatus.Unavailable, ScheduleGovernedPayloadResolutionStatus.Unavailable),
            ((ScheduleStoreReadStatus)999, ScheduleGovernedPayloadResolutionStatus.Corrupt),
        })
        {
            store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(status, null, null));
            Assert.Equal(expected, (await source.ResolveAsync(reference)).Status);
        }

        store.ReadBehavior = (_, _) => throw new InvalidOperationException("read failed");
        Assert.Equal(ScheduleGovernedPayloadResolutionStatus.Unavailable, (await source.ResolveAsync(reference)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store.ReadBehavior = (_, token) => Task.FromCanceled<ScheduleStoreReadResult>(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ResolveAsync(reference, cancellation.Token));
        Assert.Equal(6, store.ReadCallCount);
        Assert.Equal(0, graphStore.ReadArtifactCallCount);
    }

    [Fact]
    public async Task Canonical_payload_and_graph_revision_mismatches_fail_closed()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore();
        var graphStore = new ScriptedGovernedLoopGraphRevisionStore();
        var source = new GovernedLoopSchedulePayloadSource(store, graphStore);
        var reference = context.Definition.Payload.GovernedReference;

        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition with
            {
                Payload = new SchedulePayloadReference("payload/other-schedule", context.Definition.Payload.ContentHash),
            },
            null));
        Assert.Equal(ScheduleGovernedPayloadResolutionStatus.Corrupt, (await source.ResolveAsync(reference)).Status);
        Assert.Equal(1, store.ReadCallCount);
        Assert.Equal(0, graphStore.ReadArtifactCallCount);

        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Found, context.Definition, null));
        var alternateRevision = GovernedLoopRevisionReference.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            context.Definition.Target.GovernedPublication!.Revision.GraphId,
            "different-revision",
            new string('a', 64));
        var alternatePublication = GovernedLoopRevisionPublicationPinFactory.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            alternateRevision,
            "publish-different-revision",
            new string('b', 64));
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(
            alternatePublication,
            context.Definition.Target.AuthorityGrant,
            out var alternateTarget,
            out var alternateTargetValidation), string.Join(',', alternateTargetValidation.Errors.Select(error => error.Code)));
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition with { Target = alternateTarget! },
            null));
        graphStore.ReadArtifactBehavior = (_, _) => Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            1,
            context.BindingResolution.Artifact));
        Assert.Equal(ScheduleGovernedPayloadResolutionStatus.Corrupt, (await source.ResolveAsync(reference)).Status);
        Assert.Equal(2, store.ReadCallCount);
        Assert.Equal(1, graphStore.ReadArtifactCallCount);

        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Found, context.Definition, null));
        foreach (var (status, expected) in new[]
        {
            (GovernedLoopRevisionStoreReadStatus.NotFound, ScheduleGovernedPayloadResolutionStatus.NotFound),
            (GovernedLoopRevisionStoreReadStatus.Unavailable, ScheduleGovernedPayloadResolutionStatus.Unavailable),
            (GovernedLoopRevisionStoreReadStatus.Ambiguous, ScheduleGovernedPayloadResolutionStatus.Corrupt),
            (GovernedLoopRevisionStoreReadStatus.Ready, ScheduleGovernedPayloadResolutionStatus.Corrupt),
        })
        {
            graphStore.ReadArtifactBehavior = (_, _) => Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(status, 1, null));
            Assert.Equal(expected, (await source.ResolveAsync(reference)).Status);
        }
        Assert.Equal(6, store.ReadCallCount);
        Assert.Equal(5, graphStore.ReadArtifactCallCount);

        graphStore.ReadArtifactBehavior = (_, _) => throw new InvalidOperationException("artifact read failed");
        Assert.Equal(ScheduleGovernedPayloadResolutionStatus.Unavailable, (await source.ResolveAsync(reference)).Status);
        Assert.Equal(7, store.ReadCallCount);
        Assert.Equal(6, graphStore.ReadArtifactCallCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        graphStore.ReadArtifactBehavior = (_, token) => Task.FromCanceled<GovernedLoopGraphRevisionArtifactReadResult>(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ResolveAsync(reference, cancellation.Token));
        Assert.Equal(8, store.ReadCallCount);
        Assert.Equal(7, graphStore.ReadArtifactCallCount);
    }

    [Fact]
    public async Task Published_purpose_is_returned_with_canonical_digest_and_bounded_content()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Found, context.Definition, null)),
        };
        var graphStore = new ScriptedGovernedLoopGraphRevisionStore
        {
            ReadArtifactBehavior = (_, _) => Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                1,
                context.BindingResolution.Artifact)),
        };
        var source = new GovernedLoopSchedulePayloadSource(store, graphStore);
        var reference = context.Definition.Payload.GovernedReference;

        var available = await source.ResolveAsync(reference);
        var content = Encoding.UTF8.GetBytes(context.BindingResolution.Artifact!.Graph.Purpose);
        Assert.Equal(ScheduleGovernedPayloadResolutionStatus.Available, available.Status);
        Assert.Equal(reference, available.GovernedReference);
        Assert.Equal(CapabilityIntegrityDigest.Compute(content), available.ContentHash);
        Assert.Equal(content, available.GetContent());

        graphStore.ReadArtifactBehavior = (_, _) => Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            1,
            context.BindingResolution.Artifact));
        var replay = await source.ResolveAsync(reference);
        Assert.Equal(available.Status, replay.Status);
        Assert.Equal(available.GovernedReference, replay.GovernedReference);
        Assert.Equal(available.ContentHash, replay.ContentHash);
        Assert.Equal(available.GetContent(), replay.GetContent());
    }
}
