using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

public sealed class GovernedLoopGrantBindingSourceTests
{
    [Fact]
    public async Task Exact_binding_is_deterministic_current_and_read_entirely_under_the_shared_fence()
    {
        var transaction = new RecordingAuthorityTransaction();
        var capabilities = new[]
        {
            AuthorityGrantApplicationTestFixture.Capability().Id.Value,
            "org.embodysense/workspace/write",
        };
        var pin = AuthorityGrantApplicationTestFixture.LoopPin(capabilityIds: capabilities);
        var publication = new PublicationSource(transaction, requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested));
        var store = new GraphStore(transaction)
        {
            Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 1, AuthorityGrantApplicationTestFixture.GraphArtifact(capabilityIds: capabilities.Reverse().ToArray())),
        };
        var source = new GovernedLoopGrantBindingSource(publication, store, transaction);

        var first = await source.ResolveAsync(pin);
        store.Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 2, AuthorityGrantApplicationTestFixture.GraphArtifact(capabilityIds: capabilities));
        var second = await source.ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Active, first.Status);
        Assert.Equal(pin, first.PublicationPin);
        Assert.Equal(first.Artifact!.Graph.OwningRole, first.OwningRole);
        Assert.Equal(first.Artifact.Graph.AuthorityCeiling.CapabilityIds, first.CapabilityIds);
        Assert.Matches("^[0-9a-f]{64}$", first.EvidenceHash);
        Assert.Equal(first.EvidenceHash, second.EvidenceHash);
        Assert.Equal(2, publication.Reads);
        Assert.Equal(2, store.Reads);
        Assert.Equal(2, transaction.OuterExecutions);
    }

    [Fact]
    public async Task Empty_graph_ceiling_is_an_exact_active_non_granting_binding()
    {
        var transaction = new RecordingAuthorityTransaction();
        var pin = AuthorityGrantApplicationTestFixture.LoopPin(capabilityIds: []);
        var publication = new PublicationSource(transaction, requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested));
        var store = new GraphStore(transaction)
        {
            Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 1, AuthorityGrantApplicationTestFixture.GraphArtifact(capabilityIds: [])),
        };

        var result = await new GovernedLoopGrantBindingSource(publication, store, transaction).ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Active, result.Status);
        Assert.Empty(result.CapabilityIds);
        Assert.Matches("^[0-9a-f]{64}$", result.EvidenceHash);
    }

    [Fact]
    public async Task Binding_source_is_reentrant_under_the_caller_owned_shared_fence()
    {
        var transaction = new RecordingAuthorityTransaction();
        var pin = AuthorityGrantApplicationTestFixture.LoopPin();
        var publication = new PublicationSource(transaction, requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested));
        var store = new GraphStore(transaction)
        {
            Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 1, AuthorityGrantApplicationTestFixture.GraphArtifact()),
        };
        var source = new GovernedLoopGrantBindingSource(publication, store, transaction);

        var result = await transaction.ExecuteAsync(token => source.ResolveAsync(pin, token));

        Assert.Equal(AuthorityGrantDependencyStatus.Active, result.Status);
        Assert.Equal(1, transaction.OuterExecutions);
    }

    [Fact]
    public async Task Publication_and_graph_artifact_substitution_fail_closed_without_cached_reads()
    {
        var transaction = new RecordingAuthorityTransaction();
        var pin = AuthorityGrantApplicationTestFixture.LoopPin();
        var publication = new PublicationSource(transaction, requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested));
        var store = new GraphStore(transaction)
        {
            Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 1, AuthorityGrantApplicationTestFixture.GraphArtifact(capabilityIds: [])),
        };
        var source = new GovernedLoopGrantBindingSource(publication, store, transaction);

        var substitutedArtifact = await source.ResolveAsync(pin);
        store.Result = new(GovernedLoopRevisionStoreReadStatus.Ready, 2, AuthorityGrantApplicationTestFixture.GraphArtifact());
        publication.Resolve = _ => AuthorityGrantApplicationTestFixture.PublishedLoop(AuthorityGrantApplicationTestFixture.LoopPin(capabilityIds: []));
        var substitutedPublication = await source.ResolveAsync(pin);
        publication.Resolve = requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested);
        var recovered = await source.ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, substitutedArtifact.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, substitutedPublication.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Active, recovered.Status);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task Closed_store_postures_and_cancellation_are_preserved()
    {
        var transaction = new RecordingAuthorityTransaction();
        var pin = AuthorityGrantApplicationTestFixture.LoopPin();
        var publication = new PublicationSource(transaction, requested => AuthorityGrantApplicationTestFixture.PublishedLoop(requested));
        var store = new GraphStore(transaction)
        {
            Result = new(GovernedLoopRevisionStoreReadStatus.NotFound, 0, null),
        };
        var source = new GovernedLoopGrantBindingSource(publication, store, transaction);

        var invalid = await source.ResolveAsync(null);
        var missing = await source.ResolveAsync(pin);
        store.Result = new(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null);
        var unavailable = await source.ResolveAsync(pin);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(AuthorityGrantDependencyStatus.Invalid, invalid.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.NotFound, missing.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Unavailable, unavailable.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ResolveAsync(pin, cancellation.Token));
    }

    private sealed class RecordingAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        private readonly AsyncLocal<int> _depth = new();

        internal bool IsHeld => _depth.Value > 0;
        internal int OuterExecutions { get; private set; }

        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHeld)
            {
                return await operation(cancellationToken);
            }

            OuterExecutions++;
            _depth.Value++;
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
            }
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PublicationSource(
        RecordingAuthorityTransaction transaction,
        Func<GovernedLoopRevisionPublicationPin?, GovernedLoopPublishedRevisionResolution> resolve) : IGovernedLoopPublishedRevisionSource
    {
        internal Func<GovernedLoopRevisionPublicationPin?, GovernedLoopPublishedRevisionResolution> Resolve { get; set; } = resolve;
        internal int Reads { get; private set; }

        public Task<GovernedLoopPublishedRevisionResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
        {
            Assert.True(transaction.IsHeld);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Resolve(pin));
        }
    }

    private sealed class GraphStore(RecordingAuthorityTransaction transaction) : IGovernedLoopGraphRevisionStore
    {
        internal GovernedLoopGraphRevisionArtifactReadResult Result { get; set; } = null!;
        internal int Reads { get; private set; }

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default)
        {
            Assert.True(transaction.IsHeld);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Result);
        }

        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(string graphId, string operationId, string lifecycleRequestHash, string authoringRequestHash, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(GovernedLoopGraphRevisionStoreMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
