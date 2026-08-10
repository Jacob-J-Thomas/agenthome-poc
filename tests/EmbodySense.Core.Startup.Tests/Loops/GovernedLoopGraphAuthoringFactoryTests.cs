using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopGraphAuthoringFactoryTests
{
    [Fact]
    public async Task Factory_reuses_the_exact_supplied_authority_transaction()
    {
        var transaction = new RecordingAuthorityTransaction();

        var service = GovernedLoopGraphAuthoringFactory.Create(
            new UnusedRevisionStore(),
            new UnusedNodeCatalog(),
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer(),
            transaction);
        var result = await service.MutateAsync(null);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Invalid, result.Status);
        Assert.Equal(1, transaction.ExecuteCount);
    }

    [Fact]
    public void Factory_rejects_missing_server_owned_dependencies()
    {
        var store = new UnusedRevisionStore();
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();
        var transaction = new RecordingAuthorityTransaction();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, catalog, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, null!, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, null!, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, null!, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, authorizer, null!));
    }

    private sealed class UnusedRevisionStore : IGovernedLoopGraphRevisionStore
    {
        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(string graphId, string operationId, string lifecycleRequestHash, string authoringRequestHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(GovernedLoopGraphRevisionStoreMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedNodeCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAuthorityProvider : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(string roleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(GovernedLoopRevisionActorAuthorizationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public int ExecuteCount { get; private set; }

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return operation(cancellationToken);
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
