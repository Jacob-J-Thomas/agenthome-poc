using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopGraphAuthoringFactoryTests
{
    [Fact]
    public void Factory_composes_public_authoring_service_without_mutating_workspace()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var service = GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new UnusedRevisionStore(),
            new UnusedNodeCatalog(),
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer());

        Assert.NotNull(service);
        Assert.False(Directory.Exists(paths.AgentPath));
    }

    [Fact]
    public void Factory_rejects_missing_server_owned_dependencies()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new UnusedRevisionStore();
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, store, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, null!, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, store, null!, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, store, catalog, null!, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, store, catalog, authority, null!));
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
}
