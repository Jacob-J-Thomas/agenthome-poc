using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class ScheduleCurrentEvidenceTestContext :
    IGovernedLoopGrantBindingSource,
    IAuthorityGrantResolver,
    IAuthorityGrantProfileSource,
    ICapabilityCatalogStore,
    IScheduleGovernedPayloadSource,
    ICapabilityAuthorityTransaction
{
    internal static readonly DateTimeOffset ObservedAtUtc = new(2026, 8, 11, 14, 31, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset GrantEvaluatedAtUtc = ObservedAtUtc.AddMilliseconds(25);
    private int _fenceDepth;

    private ScheduleCurrentEvidenceTestContext(
        IReadOnlyList<AuthorityBoundaryCondition>? boundaryConditions,
        bool allowsRecurrence,
        bool grantIncludesAdapter)
    {
        Payload = "bounded scheduled input"u8.ToArray();
        var descriptor = Assert.Single(BuiltInCapabilityCatalog.Descriptors, value => value.Id.Value == "org.embodysense/triggers/time");
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var descriptorIdentity, out var descriptorValidation), string.Join(',', descriptorValidation.Errors.Select(error => error.Code)));
        Adapter = new TriggerAdapterReference(descriptorIdentity!, descriptor.Implementation);
        CatalogEntry = new CapabilityCatalogEntry(
            descriptor,
            new CapabilityLifecycleSnapshot(
                CapabilityLifecycleSnapshot.CurrentSchemaVersion,
                descriptorIdentity!,
                CapabilityDeclarationState.Declared,
                CapabilityInstallationState.Installed,
                CapabilityEnablementState.Enabled,
                CapabilityHealthState.Healthy,
                CapabilityRetirementState.Active,
                CapabilityTrustState.Verified),
            1,
            ObservedAtUtc.AddHours(-1),
            "seed-schedule-adapter");

        var rolePin = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("operator", 1), Hash64('1'));
        var candidate = new GovernedLoopGraphCandidate(
            1,
            "daily-reflection",
            "revision-8",
            "Execute one bounded scheduled operation.",
            rolePin,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([descriptor.Id.Value]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            [
                new GovernedLoopNodeDefinition(
                    "trigger",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "time-trigger", 1),
                    [new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                new GovernedLoopNodeDefinition(
                    "exit",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                    [
                        new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
            ],
            [new GovernedLoopControlEdgeDefinition("trigger-to-exit", "trigger", "exit", GovernedLoopControlCondition.Always)],
            [new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "request")],
            new GovernedLoopOutputContract(
                "Return the bounded result.",
                [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Scheduled loop",
                "Test-only scheduled loop.",
                [
                    new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 100, 0),
                ]),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
        var graph = Assert.IsType<GovernedLoopGraphDefinition>(GovernedLoopGraphNormalizer.Normalize(candidate).Graph);
        var revisionArtifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            "create-scheduled-loop",
            "owner",
            ObservedAtUtc.AddHours(-2));
        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            revisionArtifact,
            graph);
        Publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            graph.RevisionReference,
            "publish-scheduled-loop",
            Hash64('2'));

        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("7", out var profileRevision, out _));
        Profile = new AuthorityProfileReference(profileId!, profileRevision!);
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash64('3'), out var profileHash, out _));
        Assert.True(AuthorityGrantId.TryParse("daily-reflection-grant", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("4", out var grantRevision, out _));
        Assert.True(AuthorityGrantRevision.TryParse("3", out var predecessorRevision, out _));
        Assert.True(AuthorityActorId.TryParse("owner", out var actor, out _));
        Assert.True(AuthorityPurpose.TryParse("Permit one bounded recurring schedule.", out var purpose, out _));
        Ceiling = new AuthorityCeiling(
            grantIncludesAdapter ? [descriptorIdentity!] : [],
            [],
            0,
            CapabilitySideEffectClass.None,
            allowsRecurrence,
            false,
            false);
        ProfileValue = new AuthorityProfile(
            1,
            profileId!,
            profileRevision!,
            AuthorityProfileStatus.Active,
            purpose!,
            new AuthorityProvenance(actor!, AuthorityProvenanceKind.UserDeclaration),
            ObservedAtUtc.AddHours(-2),
            null,
            Ceiling,
            boundaryConditions ?? []);
        Assert.True(AuthorityProfileHash.TryCompute(ProfileValue, out profileHash, out var profileValidation), string.Join(',', profileValidation.Errors.Select(error => error.Code)));
        var grant = AuthorityGrantHash.Apply(new AuthorityGrant(
            1,
            grantId!,
            grantRevision!,
            predecessorRevision!,
            "sha256:" + Hash64('4'),
            AuthorityGrantLifecycleStatus.Active,
            new AuthorityGrantBinding(new AuthorityGrantProfilePin(Profile, profileHash!), rolePin, Publication),
            Ceiling,
            new AuthorityGrantBoundary(ObservedAtUtc.AddHours(-1), ObservedAtUtc.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            actor!,
            purpose!,
            ObservedAtUtc.AddHours(-1),
            string.Empty));
        GrantReference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(Publication, GrantReference, out var target, out var targetValidation), string.Join(',', targetValidation.Errors.Select(error => error.Code)));
        Target = target!;
        BindingResolution = new GovernedLoopGrantBindingResolution(
            AuthorityGrantDependencyStatus.Active,
            Publication,
            artifact,
            rolePin,
            artifact.Graph.AuthorityCeiling.CapabilityIds,
            Hash64('5'));
        GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            GrantReference,
            grant,
            Ceiling,
            Hash64('6'),
            GrantEvaluatedAtUtc);
        ProfileResolution = new AuthorityGrantProfileResolution(
            AuthorityGrantDependencyStatus.Active,
            grant.Binding.Profile,
            ProfileValue,
            Hash64('8'));
        CatalogRead = AvailableCatalog(CatalogEntry);
        PayloadResolution = AvailablePayload(Payload);
        Definition = CreateDefinition();
        Occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            new DateTime(2026, 8, 11, 9, 30, 0, DateTimeKind.Unspecified),
            new DateTimeOffset(2026, 8, 11, 14, 30, 0, TimeSpan.Zero),
            Definition.TimeZone);
    }

    internal ScheduleDefinition Definition { get; }
    internal ScheduleOccurrence Occurrence { get; }
    internal TriggerAdapterReference Adapter { get; }
    internal TriggerLoopReference Target { get; }
    internal GovernedLoopRevisionPublicationPin Publication { get; }
    internal AuthorityGrantReference GrantReference { get; }
    internal AuthorityProfileReference Profile { get; }
    internal AuthorityProfile ProfileValue { get; }
    internal AuthorityCeiling Ceiling { get; set; }
    internal CapabilityCatalogEntry CatalogEntry { get; }
    internal byte[] Payload { get; }
    internal GovernedLoopGrantBindingResolution BindingResolution { get; set; }
    internal AuthorityGrantResolution GrantResolution { get; set; }
    internal AuthorityGrantProfileResolution ProfileResolution { get; set; }
    internal CapabilityCatalogReadResult CatalogRead { get; set; }
    internal Queue<CapabilityCatalogReadResult> CatalogReads { get; } = new();
    internal ScheduleGovernedPayloadResolution PayloadResolution { get; set; }
    internal Exception? TargetFailure { get; set; }
    internal Exception? GrantFailure { get; set; }
    internal Action? BeforeGrantResolve { get; set; }
    internal Exception? ProfileFailure { get; set; }
    internal Exception? CatalogFailure { get; set; }
    internal Action? BeforeCatalogRead { get; set; }
    internal Exception? PayloadFailure { get; set; }
    internal Action? BeforePayloadResolve { get; set; }
    internal int FenceCount { get; private set; }
    internal int TargetReadCount { get; private set; }
    internal int GrantReadCount { get; private set; }
    internal int ProfileReadCount { get; private set; }
    internal int CatalogReadCount { get; private set; }
    internal int PayloadReadCount { get; private set; }
    internal bool AllReadsInsideFence { get; private set; } = true;
    internal bool PayloadReadInsideFence { get; private set; }
    internal string? RequestedPayloadReference { get; private set; }

    internal static ScheduleCurrentEvidenceTestContext Create(
        IReadOnlyList<AuthorityBoundaryCondition>? boundaryConditions = null,
        bool allowsRecurrence = true,
        bool grantIncludesAdapter = true)
        => new(boundaryConditions, allowsRecurrence, grantIncludesAdapter);

    internal ScheduleCurrentEvidenceAdapter AdapterUnderTest()
        => new(Definition.WorkspaceId, this, this, this, this, this, this, new FixedTimeProvider(GrantEvaluatedAtUtc.AddMilliseconds(25)));

    internal ScheduleCurrentEvidenceAdapter AdapterUnderTest(TimeProvider timeProvider)
        => new(Definition.WorkspaceId, this, this, this, this, this, this, timeProvider);

    internal CapabilityCatalogReadResult AvailableCatalog(params CapabilityCatalogEntry[] entries)
        => new(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(1, entries, null), "available");

    internal CapabilityCatalogEntry CatalogEntryWithId(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var id, out _));
        var descriptor = CatalogEntry.Descriptor with { Id = id! };
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        return CatalogEntry with
        {
            Descriptor = descriptor,
            Lifecycle = CatalogEntry.Lifecycle with { DescriptorIdentity = identity! },
        };
    }

    internal ScheduleGovernedPayloadResolution AvailablePayload(byte[] payload)
        => new(
            ScheduleGovernedPayloadResolutionStatus.Available,
            Definition?.Payload.GovernedReference ?? "payload/daily-reflection",
            CapabilityIntegrityDigest.Compute(payload),
            payload);

    public Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TargetReadCount++;
        AllReadsInsideFence &= _fenceDepth > 0;
        if (TargetFailure is not null)
        {
            throw TargetFailure;
        }

        return Task.FromResult(BindingResolution);
    }

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GrantReadCount++;
        AllReadsInsideFence &= _fenceDepth > 0;
        BeforeGrantResolve?.Invoke();
        if (GrantFailure is not null)
        {
            throw GrantFailure;
        }

        return Task.FromResult(GrantResolution);
    }

    public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogReadCount++;
        AllReadsInsideFence &= _fenceDepth > 0;
        BeforeCatalogRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (CatalogFailure is not null)
        {
            throw CatalogFailure;
        }

        return Task.FromResult(CatalogReads.Count > 0 ? CatalogReads.Dequeue() : CatalogRead);
    }

    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Current evidence never mutates the capability catalog.");

    public Task<AuthorityGrantProfileResolution> ResolveAsync(AuthorityGrantProfilePin? pin, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProfileReadCount++;
        AllReadsInsideFence &= _fenceDepth > 0;
        if (ProfileFailure is not null)
        {
            throw ProfileFailure;
        }

        return Task.FromResult(ProfileResolution);
    }

    public Task<ScheduleGovernedPayloadResolution> ResolveAsync(string governedReference, CancellationToken cancellationToken = default)
    {
        PayloadReadCount++;
        RequestedPayloadReference = governedReference;
        PayloadReadInsideFence |= _fenceDepth > 0;
        BeforePayloadResolve?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (PayloadFailure is not null)
        {
            throw PayloadFailure;
        }

        return Task.FromResult(PayloadResolution);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FenceCount++;
        _fenceDepth++;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _fenceDepth--;
        }
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Current evidence uses one bounded transaction, not a retained lease.");

    private ScheduleDefinition CreateDefinition()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        var payload = new SchedulePayloadReference("payload/daily-reflection", CapabilityIntegrityDigest.Compute(Payload));
        var timeZone = new ScheduleTimeZoneReference("America/Chicago", Hash64('7'));
        return new ScheduleDefinition(
            ScheduleDefinition.CurrentSchemaVersion,
            scheduleId!,
            1,
            Target,
            Adapter,
            actorId!,
            "scheduler",
            "workspace-1",
            "operator",
            Profile,
            payload,
            SchedulePriority.Normal,
            new ScheduleRecurrenceRule(
                ScheduleRecurrenceKind.Daily,
                new DateTime(2026, 8, 11, 9, 30, 0, DateTimeKind.Unspecified),
                null),
            timeZone,
            new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.ShiftForward, ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
            new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.CatchUp, 3),
            ScheduleOverlapPolicy.DeferOne,
            true);
    }

    private static string Hash64(char value) => new(value, 64);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
