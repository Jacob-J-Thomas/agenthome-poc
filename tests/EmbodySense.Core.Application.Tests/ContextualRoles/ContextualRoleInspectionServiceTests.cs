using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Tests.ContextualRoles;

public sealed class ContextualRoleInspectionServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Catalog_projects_ready_ineligible_workspace_and_source_posture_without_granting_authority()
    {
        var ports = new Ports();
        ports.CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available,
        [
            Entry("analyst", ContextualRoleLifecycleState.Active, workspaceId: "workspace-other"),
            Entry("missing", ContextualRoleLifecycleState.Active, sourceId: "missing"),
            Entry("reviewer", ContextualRoleLifecycleState.Active, sourceId: "nearest-agents", sourceKind: ContextualRoleInstructionSourceKind.AgentsMarkdown),
            Entry("writer", ContextualRoleLifecycleState.Disabled)
        ], "writer");
        ports.Probe = source => source.ReferenceId == "missing"
            ? ContextualRoleInstructionSourceProbeStatus.Missing
            : ContextualRoleInstructionSourceProbeStatus.Ready;

        var result = await Service(ports).ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 4));

        Assert.Equal(ContextualRoleCatalogReadStatus.Available, result.Status);
        Assert.Equal("writer", result.NextCursor);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch, result.Entries[0].SourceStatus);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Missing, result.Entries[1].SourceStatus);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, result.Entries[2].SourceStatus);
        Assert.True(result.Entries[2].IsAdmissionReady);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ineligible, result.Entries[3].SourceStatus);
        Assert.All(result.Entries, entry =>
        {
            Assert.Empty(entry.Dependents);
            Assert.False(entry.AreDependentsComplete);
            Assert.False(entry.DependentsTruncated);
        });
        Assert.Equal(2, ports.ProbeCalls);
    }

    [Theory]
    [InlineData(ContextualRoleCatalogReadStatus.Invalid, ContextualRoleCatalogReadStatus.Invalid)]
    [InlineData(ContextualRoleCatalogReadStatus.Unavailable, ContextualRoleCatalogReadStatus.Unavailable)]
    [InlineData(ContextualRoleCatalogReadStatus.Ambiguous, ContextualRoleCatalogReadStatus.Ambiguous)]
    [InlineData(ContextualRoleCatalogReadStatus.Unknown, ContextualRoleCatalogReadStatus.Ambiguous)]
    public async Task Catalog_failures_return_no_partial_posture(ContextualRoleCatalogReadStatus status, ContextualRoleCatalogReadStatus expected)
    {
        var ports = new Ports { CatalogResult = new ContextualRoleCatalogReadResult(status, [Entry("reviewer")], "reviewer") };

        var result = await Service(ports).ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Entries);
        Assert.Null(result.NextCursor);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public async Task Catalog_rejects_null_unbounded_unordered_and_inconsistent_port_pages()
    {
        var ports = new Ports();
        var service = Service(ports);
        ports.CatalogResult = null!;
        var nullPage = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));
        ports.CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [Entry("analyst"), Entry("reviewer")], null);
        var unbounded = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));
        ports.CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [Entry("reviewer"), Entry("analyst")], null);
        var unordered = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 2));
        var inconsistent = Entry("reviewer") with { Lifecycle = Lifecycle("other", 1, ContextualRoleLifecycleState.Active) };
        ports.CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [inconsistent], null);
        var mismatched = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));
        ports.CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [Entry("reviewer")], "other");
        var invalidCursor = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));

        Assert.All([nullPage, unbounded, unordered, mismatched, invalidCursor], result => Assert.Equal(ContextualRoleCatalogReadStatus.Ambiguous, result.Status));
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Theory]
    [InlineData(ContextualRoleLifecycleReadStatus.Unavailable, ContextualRoleCatalogReadStatus.Unavailable)]
    [InlineData(ContextualRoleLifecycleReadStatus.Ambiguous, ContextualRoleCatalogReadStatus.Ambiguous)]
    public async Task Catalog_returns_no_partial_posture_when_lifecycle_confirmation_fails(ContextualRoleLifecycleReadStatus lifecycleStatus, ContextualRoleCatalogReadStatus expected)
    {
        var ports = new Ports
        {
            CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [Entry("reviewer")], null),
            LifecycleResult = new ContextualRoleLifecycleReadResult(lifecycleStatus, null)
        };

        var result = await Service(ports).ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Entries);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task Catalog_returns_no_partial_posture_when_lifecycle_changes_during_source_inspection()
    {
        var ports = new Ports
        {
            CatalogResult = new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [Entry("reviewer")], null)
        };
        ports.Probe = _ =>
        {
            ports.LifecycleResult = new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, Lifecycle("reviewer", 1, ContextualRoleLifecycleState.Disabled));
            return ContextualRoleInstructionSourceProbeStatus.Ready;
        };

        var result = await Service(ports).ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 1));

        Assert.Equal(ContextualRoleCatalogReadStatus.Ambiguous, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Catalog_rejects_malformed_shape_before_the_reader()
    {
        var ports = new Ports();
        var service = Service(ports);

        var results = await Task.WhenAll(
            service.ReadCatalogAsync(null!),
            service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest("../unsafe", 1)),
            service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 0)),
            service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, ContextualRoleCatalogLimits.MaximumPageSize + 1)));

        Assert.All(results, result => Assert.Equal(ContextualRoleCatalogReadStatus.Invalid, result.Status));
        Assert.Equal(0, ports.CatalogReads);
    }

    [Fact]
    public async Task Cancellation_precedes_catalog_and_exact_request_validation()
    {
        var ports = new Ports();
        var service = Service(ports);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadCatalogAsync(null!, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InspectAsync(null!, cancellation.Token));
        Assert.Equal(0, ports.CatalogReads);
        Assert.Equal(0, ports.RevisionReads);
    }

    [Fact]
    public async Task Exact_inspection_rejects_malformed_shape_before_any_port_read()
    {
        var ports = new Ports();
        var service = Service(ports);

        var results = await Task.WhenAll(
            service.InspectAsync(null!),
            service.InspectAsync(new ContextualRoleInspectionRequest("../unsafe", 1, new string('a', 64))),
            service.InspectAsync(new ContextualRoleInspectionRequest("reviewer", 0, new string('a', 64))),
            service.InspectAsync(new ContextualRoleInspectionRequest("reviewer", 1, "not-a-hash")),
            service.InspectAsync(new ContextualRoleInspectionRequest("reviewer", 1, new string('A', 64))));

        Assert.All(results, result => Assert.Equal(ContextualRoleInspectionStatus.Invalid, result.Status));
        Assert.Equal(0, ports.RevisionReads);
        Assert.Equal(0, ports.LifecycleReads);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Theory]
    [InlineData(ContextualRoleRevisionReadStatus.NotFound, ContextualRoleInspectionStatus.NotFound)]
    [InlineData(ContextualRoleRevisionReadStatus.Invalid, ContextualRoleInspectionStatus.Invalid)]
    [InlineData(ContextualRoleRevisionReadStatus.Unavailable, ContextualRoleInspectionStatus.Unavailable)]
    [InlineData(ContextualRoleRevisionReadStatus.Ambiguous, ContextualRoleInspectionStatus.Ambiguous)]
    [InlineData(ContextualRoleRevisionReadStatus.Unknown, ContextualRoleInspectionStatus.Ambiguous)]
    public async Task Exact_inspection_maps_revision_read_failures_without_later_reads(ContextualRoleRevisionReadStatus readStatus, ContextualRoleInspectionStatus expected)
    {
        var ports = new Ports { RevisionResult = new ContextualRoleRevisionReadResult(readStatus, null, ContextualRoleRevisionDisposition.Unknown, []) };

        var result = await Service(ports).InspectAsync(Request());

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Entry);
        Assert.Equal(0, ports.LifecycleReads);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public async Task Exact_inspection_fails_stale_hash_before_lifecycle_or_source_reads()
    {
        var ports = new Ports();

        var result = await Service(ports).InspectAsync(Request(contentHash: new string('0', 64)));

        Assert.Equal(ContextualRoleInspectionStatus.Stale, result.Status);
        Assert.Equal(0, ports.LifecycleReads);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public async Task Exact_inspection_rejects_malformed_port_evidence_as_ambiguous()
    {
        var ports = new Ports { ReturnNullRevisionResult = true };
        var nullRevisionRead = await Service(ports).InspectAsync(Request());
        var otherRevision = Revision("other");
        ports.ReturnNullRevisionResult = false;
        ports.RevisionResult = Found(otherRevision);
        var mismatchedRevision = await Service(ports).InspectAsync(Request(contentHash: otherRevision.ContentHash));
        var requestedRevision = Revision("reviewer");
        ports.RevisionResult = Found(requestedRevision);
        ports.LifecycleResult = new ContextualRoleLifecycleReadResult(
            ContextualRoleLifecycleReadStatus.Found,
            Lifecycle("reviewer", 1, ContextualRoleLifecycleState.Active) with { LastOperationId = "../unsafe" });
        var malformedLifecycle = await Service(ports).InspectAsync(Request());
        ports.LifecycleResult = new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, Lifecycle("reviewer", 1, ContextualRoleLifecycleState.Active));
        ports.RevisionResult = new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Found, requestedRevision, ContextualRoleRevisionDisposition.Replaced, []);
        var inconsistentDisposition = await Service(ports).InspectAsync(Request());

        Assert.Equal(ContextualRoleInspectionStatus.Ambiguous, nullRevisionRead.Status);
        Assert.Equal(ContextualRoleInspectionStatus.Ambiguous, mismatchedRevision.Status);
        Assert.Equal(ContextualRoleInspectionStatus.Ambiguous, malformedLifecycle.Status);
        Assert.Equal(ContextualRoleInspectionStatus.Ambiguous, inconsistentDisposition.Status);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Theory]
    [InlineData(ContextualRoleLifecycleReadStatus.NotFound, ContextualRoleInspectionStatus.Stale)]
    [InlineData(ContextualRoleLifecycleReadStatus.Invalid, ContextualRoleInspectionStatus.Invalid)]
    [InlineData(ContextualRoleLifecycleReadStatus.Unavailable, ContextualRoleInspectionStatus.Unavailable)]
    [InlineData(ContextualRoleLifecycleReadStatus.Ambiguous, ContextualRoleInspectionStatus.Ambiguous)]
    [InlineData(ContextualRoleLifecycleReadStatus.Unknown, ContextualRoleInspectionStatus.Ambiguous)]
    public async Task Exact_inspection_maps_lifecycle_failures_without_source_reads(ContextualRoleLifecycleReadStatus readStatus, ContextualRoleInspectionStatus expected)
    {
        var ports = new Ports { LifecycleResult = new ContextualRoleLifecycleReadResult(readStatus, null) };

        var result = await Service(ports).InspectAsync(Request());

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Entry);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public async Task Exact_inspection_rejects_a_replaced_revision_as_stale()
    {
        var ports = new Ports
        {
            LifecycleResult = new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, Lifecycle("reviewer", 2, ContextualRoleLifecycleState.Active))
        };

        var result = await Service(ports).InspectAsync(Request());

        Assert.Equal(ContextualRoleInspectionStatus.Stale, result.Status);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public async Task Exact_inspection_fails_stale_when_lifecycle_changes_during_source_inspection()
    {
        var ports = new Ports();
        ports.Probe = _ =>
        {
            ports.LifecycleResult = new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, Lifecycle("reviewer", 1, ContextualRoleLifecycleState.Disabled));
            return ContextualRoleInstructionSourceProbeStatus.Ready;
        };

        var result = await Service(ports).InspectAsync(Request());

        Assert.Equal(ContextualRoleInspectionStatus.Stale, result.Status);
        Assert.Null(result.Entry);
    }

    [Theory]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Ready, ContextualRoleInspectionStatus.Ready, true)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Missing, ContextualRoleInspectionStatus.SourceMissing, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Unsupported, ContextualRoleInspectionStatus.SourceUnsupported, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Oversized, ContextualRoleInspectionStatus.SourceOversized, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Substituted, ContextualRoleInspectionStatus.SourceSubstituted, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Unavailable, ContextualRoleInspectionStatus.Unavailable, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Ambiguous, ContextualRoleInspectionStatus.Ambiguous, false)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Unknown, ContextualRoleInspectionStatus.Ambiguous, false)]
    public async Task Exact_inspection_maps_every_source_posture(ContextualRoleInstructionSourceProbeStatus sourceStatus, ContextualRoleInspectionStatus expected, bool ready)
    {
        var ports = new Ports { Probe = _ => sourceStatus };

        var result = await Service(ports).InspectAsync(Request());

        Assert.Equal(expected, result.Status);
        Assert.Equal(ready, result.Entry!.IsAdmissionReady);
        Assert.Equal(sourceStatus, result.Entry.SourceStatus);
        Assert.Empty(result.Entry.Dependents);
        Assert.False(result.Entry.AreDependentsComplete);
        Assert.False(result.Entry.DependentsTruncated);
        Assert.Equal(1, ports.ProbeCalls);
    }

    [Theory]
    [InlineData(ContextualRoleLifecycleState.Disabled, ContextualRoleStatus.Published, "workspace-one", ContextualRoleInspectionStatus.Ineligible)]
    [InlineData(ContextualRoleLifecycleState.Tombstoned, ContextualRoleStatus.Published, "workspace-one", ContextualRoleInspectionStatus.Ineligible)]
    [InlineData(ContextualRoleLifecycleState.Active, ContextualRoleStatus.Draft, "workspace-one", ContextualRoleInspectionStatus.Ineligible)]
    [InlineData(ContextualRoleLifecycleState.Active, ContextualRoleStatus.Published, "workspace-other", ContextualRoleInspectionStatus.WorkspaceMismatch)]
    public async Task Ineligible_or_cross_workspace_roles_never_probe_sources(ContextualRoleLifecycleState lifecycle, ContextualRoleStatus revisionStatus, string workspaceId, ContextualRoleInspectionStatus expected)
    {
        var revision = Revision("reviewer", status: revisionStatus, workspaceId: workspaceId);
        var ports = new Ports
        {
            RevisionResult = Found(revision, lifecycle switch
            {
                ContextualRoleLifecycleState.Disabled => ContextualRoleRevisionDisposition.Disabled,
                ContextualRoleLifecycleState.Tombstoned => ContextualRoleRevisionDisposition.Tombstoned,
                _ => ContextualRoleRevisionDisposition.Active
            }),
            LifecycleResult = new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, Lifecycle("reviewer", 1, lifecycle))
        };

        var result = await Service(ports).InspectAsync(Request(contentHash: revision.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.False(result.Entry!.IsAdmissionReady);
        Assert.Equal(0, ports.ProbeCalls);
    }

    [Fact]
    public void Constructor_requires_a_bounded_workspace_and_every_port()
    {
        var ports = new Ports();

        Assert.Throws<ArgumentException>(() => new ContextualRoleInspectionService("../unsafe", ports, ports, ports, ports));
        Assert.Throws<ArgumentNullException>(() => new ContextualRoleInspectionService("workspace-one", null!, ports, ports, ports));
        Assert.Throws<ArgumentNullException>(() => new ContextualRoleInspectionService("workspace-one", ports, null!, ports, ports));
        Assert.Throws<ArgumentNullException>(() => new ContextualRoleInspectionService("workspace-one", ports, ports, null!, ports));
        Assert.Throws<ArgumentNullException>(() => new ContextualRoleInspectionService("workspace-one", ports, ports, ports, null!));
    }

    [Fact]
    public void Inspection_models_take_defensive_dependency_and_catalog_snapshots()
    {
        var dependent = new ContextualRoleDependencyImpact("loop", "loop-one", 3);
        var dependents = new List<ContextualRoleDependencyImpact> { dependent };
        var entry = Entry("reviewer");
        var inspected = new ContextualRoleInspectionEntry(entry.Revision, entry.Lifecycle, ContextualRoleInstructionSourceProbeStatus.Ready, true, true, dependents, true, false);
        var inspectedEntries = new List<ContextualRoleInspectionEntry> { inspected };
        var catalog = new ContextualRoleInspectionCatalogResult(ContextualRoleCatalogReadStatus.Available, inspectedEntries, null);
        var nullDependents = new ContextualRoleInspectionEntry(entry.Revision, entry.Lifecycle, ContextualRoleInstructionSourceProbeStatus.Ready, true, true, null!, true, false);

        dependents.Clear();
        inspectedEntries.Clear();

        Assert.Equal("loop", dependent.Kind);
        Assert.Equal("loop-one", dependent.Identity);
        Assert.Equal(3, dependent.Revision);
        Assert.Same(dependent, Assert.Single(inspected.Dependents));
        Assert.Same(inspected, Assert.Single(catalog.Entries));
        Assert.Same(entry.Revision, inspected.Revision);
        Assert.Same(entry.Lifecycle, inspected.Lifecycle);
        Assert.True(inspected.IsApplicableToWorkspace);
        Assert.Empty(nullDependents.Dependents);
    }

    private static ContextualRoleInspectionService Service(Ports ports) => new("workspace-one", ports, ports, ports, ports);

    private static ContextualRoleInspectionRequest Request(string? contentHash = null)
    {
        var revision = Revision("reviewer");
        return new ContextualRoleInspectionRequest("reviewer", 1, contentHash ?? revision.ContentHash);
    }

    private static ContextualRoleCatalogEntry Entry(
        string roleId,
        ContextualRoleLifecycleState lifecycle = ContextualRoleLifecycleState.Active,
        string workspaceId = "workspace-one",
        string sourceId = "role",
        ContextualRoleInstructionSourceKind sourceKind = ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown)
    {
        var revision = Revision(roleId, workspaceId: workspaceId, sourceId: sourceId, sourceKind: sourceKind);
        return new ContextualRoleCatalogEntry(revision, Lifecycle(roleId, revision.Identity.Revision, lifecycle));
    }

    private static ContextualRoleRevision Revision(
        string roleId,
        int revision = 1,
        ContextualRoleStatus status = ContextualRoleStatus.Published,
        string workspaceId = "workspace-one",
        string sourceId = "role",
        ContextualRoleInstructionSourceKind sourceKind = ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown)
    {
        var value = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(roleId, revision),
            string.Empty,
            $"{roleId} display",
            $"{roleId} purpose",
            status,
            new ContextualRoleProvenance("user-jake", _now, _now),
            new ContextualRoleWorkspaceApplicability([workspaceId]),
            new ContextualRoleInstructionSourceReference(sourceKind, sourceId, ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(["org.embodysense/workspace/read"]));
        return ContextualRoleRevisionContentHash.Apply(value);
    }

    private static ContextualRoleLifecycleSnapshot Lifecycle(string roleId, int revision, ContextualRoleLifecycleState state)
        => new(1, roleId, new ContextualRoleRevisionIdentity(roleId, revision), state, $"create-{roleId}", ContextualRoleRevisionMutationKind.Create, _now);

    private static ContextualRoleRevisionReadResult Found(ContextualRoleRevision revision, ContextualRoleRevisionDisposition disposition = ContextualRoleRevisionDisposition.Active)
        => new(ContextualRoleRevisionReadStatus.Found, revision, disposition, []);

    private sealed class Ports : IContextualRoleCatalogReader, IContextualRoleRevisionReader, IContextualRoleLifecycleReader, IContextualRoleInstructionSourceProbe
    {
        private readonly ContextualRoleRevision _defaultRevision = Revision("reviewer");

        public ContextualRoleCatalogReadResult CatalogResult { get; set; } = new(ContextualRoleCatalogReadStatus.Available, [], null);
        public ContextualRoleRevisionReadResult? RevisionResult { get; set; }
        public bool ReturnNullRevisionResult { get; set; }
        public ContextualRoleLifecycleReadResult? LifecycleResult { get; set; }
        public Func<ContextualRoleInstructionSourceReference, ContextualRoleInstructionSourceProbeStatus> Probe { get; set; } = _ => ContextualRoleInstructionSourceProbeStatus.Ready;
        public int CatalogReads { get; private set; }
        public int RevisionReads { get; private set; }
        public int LifecycleReads { get; private set; }
        public int ProbeCalls { get; private set; }

        public Task<ContextualRoleCatalogReadResult> ReadCatalogAsync(ContextualRoleCatalogReadRequest request, CancellationToken cancellationToken = default)
        {
            CatalogReads++;
            return Task.FromResult(CatalogResult);
        }

        public Task<ContextualRoleRevisionReadResult> ReadAsync(ContextualRoleRevisionReadRequest request, CancellationToken cancellationToken = default)
        {
            RevisionReads++;
            return Task.FromResult(ReturnNullRevisionResult ? null! : RevisionResult ?? Found(_defaultRevision));
        }

        public Task<ContextualRoleLifecycleReadResult> ReadLifecycleAsync(ContextualRoleLifecycleReadRequest request, CancellationToken cancellationToken = default)
        {
            LifecycleReads++;
            var catalogLifecycle = CatalogResult.Entries.FirstOrDefault(entry => string.Equals(entry.Revision.Identity.RoleId, request.RoleId, StringComparison.Ordinal))?.Lifecycle;
            return Task.FromResult(LifecycleResult ?? new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, catalogLifecycle ?? Lifecycle("reviewer", 1, ContextualRoleLifecycleState.Active)));
        }

        public Task<ContextualRoleInstructionSourceProbeResult> ProbeAsync(ContextualRoleInstructionSourceReference source, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(new ContextualRoleInstructionSourceProbeResult(Probe(source)));
        }
    }
}
