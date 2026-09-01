using System.Reflection;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class BuiltInGovernedLoopNodeCatalogHumanReviewTests
{
    [Fact]
    public async Task Default_catalog_advertises_the_exact_human_review_shape_but_not_executable()
    {
        var snapshot = await ReadAsync(CreateCatalog());
        var descriptor = HumanReviewDescriptor(snapshot);

        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
        Assert.True(GovernedLoopHumanReviewNodeCatalogContract.HasExactCatalogStructure(descriptor));
    }

    [Fact]
    public async Task Readiness_probe_controls_human_review_executability_on_each_snapshot()
    {
        var enabled = false;
        var catalog = CreateCatalog(() => enabled);

        Assert.False(HumanReviewDescriptor(await ReadAsync(catalog)).IsExecutable);

        enabled = true;

        Assert.True(HumanReviewDescriptor(await ReadAsync(catalog)).IsExecutable);
    }

    [Fact]
    public async Task Readiness_probe_failures_fail_closed_without_changing_catalog_availability()
    {
        var catalog = CreateCatalog(() => throw new InvalidOperationException("private readiness diagnostic"));

        var snapshot = await ReadAsync(catalog);
        var descriptor = HumanReviewDescriptor(snapshot);

        Assert.True(snapshot.IsAvailable);
        Assert.False(descriptor.IsExecutable);
        Assert.DoesNotContain("private readiness diagnostic", snapshot.SourceEvidenceId, StringComparison.Ordinal);
    }

    private static GovernedLoopNodeCatalogDescriptor HumanReviewDescriptor(GovernedLoopNodeCatalogSnapshot snapshot)
        => Assert.Single(snapshot.Descriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);

    private static object CreateCatalog(Func<bool>? readinessProbe = null)
    {
        var catalogType = typeof(GovernedLoopGraphAuthoringFactory).Assembly.GetType("EmbodySense.Core.Startup.Loops.BuiltInGovernedLoopNodeCatalog", throwOnError: true)!;
        var constructor = catalogType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 6);
        return constructor.Invoke([Array.Empty<CommandActionRegistration>(), null, null, null, null, readinessProbe]);
    }

    private static async Task<GovernedLoopNodeCatalogSnapshot> ReadAsync(object catalog)
    {
        var getSnapshot = catalog.GetType().GetMethod(nameof(IGovernedLoopNodeCatalog.GetSnapshotAsync))!;
        var task = (Task<GovernedLoopNodeCatalogSnapshot>)getSnapshot.Invoke(catalog, [CancellationToken.None])!;
        return await task;
    }
}
