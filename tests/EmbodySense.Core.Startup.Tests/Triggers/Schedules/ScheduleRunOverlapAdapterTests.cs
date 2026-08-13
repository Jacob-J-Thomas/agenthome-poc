using System.Text.Json;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Tests.Loops.Execution;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class ScheduleRunOverlapAdapterTests
{
    private static readonly DateTimeOffset _observedAtUtc = new(2026, 8, 11, 12, 2, 0, TimeSpan.Zero);

    [Fact]
    public async Task Missing_run_is_clear_with_deterministic_query_bound_evidence()
    {
        var (run, target) = RunningLegacyRun();
        var store = new ScheduleOverlapRunStore();
        var adapter = new ScheduleRunOverlapAdapter(store);
        var identity = Identity();

        var first = await adapter.GetStatusAsync(target, identity, _observedAtUtc);
        var repeated = await adapter.GetStatusAsync(target, identity, _observedAtUtc);
        var later = await adapter.GetStatusAsync(target, identity, _observedAtUtc.AddTicks(1));

        Assert.Equal(ScheduleOverlapStatus.Clear, first.Status);
        Assert.Matches("^[0-9a-f]{64}$", first.EvidenceHash);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first.EvidenceHash, later.EvidenceHash);
        Assert.Equal(3, store.ReadCount);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task Exact_nonterminal_revision_is_active_while_another_immutable_revision_is_clear()
    {
        var (run, exactTarget) = RunningLegacyRun();
        var adapter = new ScheduleRunOverlapAdapter(new ScheduleOverlapRunStore(run));
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(
            run.LoopId,
            run.AdmittedDefinition.DefinitionVersion + 1,
            new string('f', 64),
            out var otherRevision,
            out _));

        var active = await adapter.GetStatusAsync(exactTarget, Identity(), _observedAtUtc);
        var clear = await adapter.GetStatusAsync(otherRevision!, Identity(), _observedAtUtc);

        Assert.Equal(ScheduleOverlapStatus.Active, active.Status);
        Assert.Equal(ScheduleOverlapStatus.Clear, clear.Status);
        Assert.Matches("^[0-9a-f]{64}$", active.EvidenceHash);
        Assert.Matches("^[0-9a-f]{64}$", clear.EvidenceHash);
        Assert.NotEqual(active.EvidenceHash, clear.EvidenceHash);
    }

    [Fact]
    public async Task Valid_nonterminal_run_for_another_loop_from_a_hostile_store_is_corrupt()
    {
        var (run, _) = RunningLegacyRun();
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(
            "queried-loop",
            run.AdmittedDefinition.DefinitionVersion,
            new string('e', 64),
            out var queriedTarget,
            out _));
        var adapter = new ScheduleRunOverlapAdapter(new ScheduleOverlapRunStore(run));

        var result = await adapter.GetStatusAsync(queriedTarget!, Identity(), _observedAtUtc);

        Assert.Equal(ScheduleOverlapStatus.Corrupt, result.Status);
        Assert.Null(result.EvidenceHash);
    }

    [Fact]
    public async Task Exact_governed_publication_and_grant_are_active_while_another_immutable_grant_is_clear()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var (run, exactTarget) = await MaterializeGovernedRunAsync(store);
        var adapter = new ScheduleRunOverlapAdapter(store);
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(
            run.SequentialAdapterBinding!.AdmissionReceipt.Intent.Publication,
            run.SequentialAdapterBinding.AdmissionReceipt.Intent.AuthorityGrant with
            {
                ContentHash = "sha256:" + new string('f', 64),
            },
            out var otherGrant,
            out _));

        var active = await adapter.GetStatusAsync(exactTarget, Identity(), _observedAtUtc);
        var clear = await adapter.GetStatusAsync(otherGrant!, Identity(), _observedAtUtc);

        Assert.Equal(ScheduleOverlapStatus.Active, active.Status);
        Assert.Equal(ScheduleOverlapStatus.Clear, clear.Status);
        Assert.Matches("^[0-9a-f]{64}$", active.EvidenceHash);
        Assert.Matches("^[0-9a-f]{64}$", clear.EvidenceHash);
        Assert.NotEqual(active.EvidenceHash, clear.EvidenceHash);
    }

    [Fact]
    public async Task Malformed_queries_and_contradictory_run_time_fail_closed()
    {
        var (run, target) = RunningLegacyRun();
        var store = new ScheduleOverlapRunStore(run);
        var adapter = new ScheduleRunOverlapAdapter(store);
        var identity = Identity();

        var invalidTarget = await adapter.GetStatusAsync(null!, identity, _observedAtUtc);
        var invalidIdentity = await adapter.GetStatusAsync(target, null!, _observedAtUtc);
        var nonUtc = await adapter.GetStatusAsync(target, identity, _observedAtUtc.ToOffset(TimeSpan.FromHours(1)));
        var tooEarly = await adapter.GetStatusAsync(target, identity, run.UpdatedAtUtc.AddTicks(-1));

        Assert.All([invalidTarget, invalidIdentity, nonUtc, tooEarly], result =>
        {
            Assert.Equal(ScheduleOverlapStatus.Corrupt, result.Status);
            Assert.Null(result.EvidenceHash);
        });
        Assert.Equal(1, store.ReadCount);
    }

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task Store_failures_are_mapped_without_fabricating_overlap(Exception failure, ScheduleOverlapStatus expected)
    {
        var (_, target) = RunningLegacyRun();
        var adapter = new ScheduleRunOverlapAdapter(new ScheduleOverlapRunStore(failure: failure));

        var result = await adapter.GetStatusAsync(target, Identity(), _observedAtUtc);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.EvidenceHash);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_the_store_read()
    {
        var (_, target) = RunningLegacyRun();
        var store = new ScheduleOverlapRunStore();
        var adapter = new ScheduleRunOverlapAdapter(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetStatusAsync(target, Identity(), _observedAtUtc, cancellation.Token));

        Assert.Equal(0, store.ReadCount);
    }

    public static TheoryData<Exception, ScheduleOverlapStatus> StoreFailures()
        => new()
        {
            { new FormatException("corrupt"), ScheduleOverlapStatus.Corrupt },
            { new InvalidDataException("corrupt"), ScheduleOverlapStatus.Corrupt },
            { new JsonException("corrupt"), ScheduleOverlapStatus.Corrupt },
            { new IOException("unavailable"), ScheduleOverlapStatus.Unavailable },
            { new UnauthorizedAccessException("unavailable"), ScheduleOverlapStatus.Unavailable },
            { new NotSupportedException("unavailable"), ScheduleOverlapStatus.Unavailable }
        };

    private static (CustomLoopRunRecord Run, TriggerLoopReference Target) RunningLegacyRun()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var definition = CustomLoopDefinitionContentHash.Apply(
            CustomLoopDefinition.CreateSeed("loop-overlap", "role-workspace", "step-only", "create-loop-overlap", now)
                with
            { ContentHash = string.Empty });
        CustomLoopRunEvent[] events =
        [
            new(1, "admitted-run-overlap", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null),
            new(2, "admission-audit-run-overlap", now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null),
            new(3, "running-run-overlap", now.AddMinutes(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null)
        ];
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-overlap",
            definition.Id,
            events.Length,
            CustomLoopRunStatus.Running,
            now,
            now.AddMinutes(1),
            null,
            "scheduler",
            new CustomLoopModelSnapshot("provider", "model"),
            "admit-run-overlap",
            WorkspaceActors.Cli,
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            new CustomLoopExecutionClock(0, now.AddMinutes(1)),
            CustomLoopRunCheckpoint.Start(),
            events,
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            out var target,
            out _));
        return (run, target!);
    }

    private static async Task<(CustomLoopRunRecord Run, TriggerLoopReference Target)> MaterializeGovernedRunAsync(
        CustomLoopRunStore store)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create(
            runId: "run-governed-overlap",
            graphId: "governed-overlap-loop",
            revisionId: "revision-overlap");
        var context = CustomLoopContextSnapshot.CreateEmpty(fixture.Receipt.RecordedAtUtc);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Execute one governed overlap probe.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            fixture.Receipt.Intent.OperationId,
            invocation.ContentHash,
            string.Empty,
            fixture.Receipt.Intent.Publication,
            fixture.Receipt.Intent.AuthorityGrant,
            fixture.Receipt.Intent.ActorId,
            fixture.Receipt.Intent.Surface));
        var intent = fixture.Receipt.Intent with { RequestHash = request.RequestHash };
        var seedEvidence = fixture.Receipt.Evidence;
        var capabilityAdmission = CreateSequentialCapabilityAdmission(fixture.Artifact, intent.WorkspaceId);
        var effectiveAuthority = new AuthorityCeiling(
            capabilityAdmission.Pins.Select(item => item.DescriptorIdentity).ToArray(),
            seedEvidence.EffectiveAuthority.DataClasses,
            seedEvidence.EffectiveAuthority.MaxTargetCount,
            seedEvidence.EffectiveAuthority.MaxSideEffectClass,
            seedEvidence.EffectiveAuthority.AllowsRecurrence,
            seedEvidence.EffectiveAuthority.AllowsExternalPublication,
            seedEvidence.EffectiveAuthority.AllowsIrreversibleAction);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            seedEvidence.SchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            seedEvidence.Binding,
            seedEvidence.GrantProfile,
            seedEvidence.GrantBoundary,
            seedEvidence.GrantDependencyEvidenceHash,
            effectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
                intent,
                effectiveAuthority,
                capabilityAdmission),
            seedEvidence.EvaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(fixture.Receipt with
        {
            Intent = intent,
            Evidence = evidence,
            ContentHash = string.Empty,
        });
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            intent.WorkspaceId,
            receipt.Evidence.Binding,
            request.OperationId,
            receipt,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            fixture.Artifact.ArtifactHash,
            fixture.Artifact.LayoutHash,
            string.Empty));
        var planResult = GovernedLoopSequentialPlanBuilder.Build(fixture.Artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(planResult.Plan);
        var materializer = new GovernedLoopSequentialRunMaterializer(
            store,
            new RecordedAudit(),
            new SequentialEventIds(),
            new FixedTimeProvider(_observedAtUtc.AddMinutes(-1)));

        var materialized = await materializer.MaterializeAsync(new GovernedLoopSequentialMaterializationRequest(
            GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
            request,
            receipt,
            fixture.Artifact,
            plan,
            invocation,
            adapterBinding));
        Assert.True(materialized.Run is not null, $"{materialized.Status}: {materialized.Detail}");
        var run = Assert.IsType<CustomLoopRunRecord>(materialized.Run);
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Ready, materialized.Status);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(
            receipt.Intent.Publication,
            receipt.Intent.AuthorityGrant,
            out var target,
            out _));
        return (run, target!);
    }

    private static CapabilityAdmissionSnapshot CreateSequentialCapabilityAdmission(
        EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopGraphRevisionArtifact artifact,
        string workspaceId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var versions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _));
        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds.Select(value =>
        {
            Assert.True(CapabilityId.TryParse(value, out var id, out _));
            return new CapabilityDependency(id!, versions!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(manifest, ConversationPublicationAuthorityTestFixture.Now) with
        {
            WorkspaceScopeId = workspaceId,
        };
    }

    private static ScheduleOccurrenceIdentity Identity()
    {
        Assert.True(ScheduleOccurrenceId.TryParse(ScheduleOccurrenceId.Prefix + new string('a', 64), out var occurrence));
        Assert.True(TriggerDeliveryId.TryParse("schedule-delivery-" + new string('b', 64), out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse("schedule-deduplication-" + new string('c', 64), out var deduplication));
        return new ScheduleOccurrenceIdentity(occurrence!, delivery!, deduplication!);
    }

    private sealed class RecordedAudit : IGovernedLoopSequentialAuditRecorder
    {
        public Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(
            string operationId,
            string evidenceHash,
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GovernedLoopSequentialAuditRecordResult(
                GovernedLoopSequentialAuditRecordStatus.Recorded,
                "recorded"));
        }
    }

    private sealed class SequentialEventIds : IGovernedLoopSequentialEventIdentityGenerator
    {
        private int _sequence;

        public string NewEventId() => $"governed-overlap-event-{Interlocked.Increment(ref _sequence)}";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
