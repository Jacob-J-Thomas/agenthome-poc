using EmbodySense.Core.Startup.Tests.Loops.Execution;
using EmbodySense.Core.Startup.Tests.Verification;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EmbodySense.Core.Startup.Tests;

public sealed class LoopRuntimeIntegrationCollectionOrdererTests
{
    [Fact]
    public void OrderTestCollections_AppliesDefaultOrderBeforeMovingLoopCollectionFirst()
    {
        ITestCollection first = new TestCollectionStub("first", new Guid("00000000-0000-0000-0000-000000000000"));
        ITestCollection second = new TestCollectionStub("second", new Guid("00000001-0000-0000-0000-000000000000"));
        ITestCollection loop = new TestCollectionStub(LoopRuntimeIntegrationCollection.Name, new Guid("00000002-0000-0000-0000-000000000000"));
        var discoveryOrder = new[] { loop, second, first };

        var defaultOrdered = new DefaultTestCollectionOrderer().OrderTestCollections(discoveryOrder).ToArray();
        var ordered = new LoopRuntimeIntegrationCollectionOrderer().OrderTestCollections(discoveryOrder).ToArray();

        Assert.Collection(
            defaultOrdered,
            collection => Assert.Same(first, collection),
            collection => Assert.Same(second, collection),
            collection => Assert.Same(loop, collection));
        Assert.NotEqual(discoveryOrder, defaultOrdered);
        Assert.Collection(
            ordered,
            collection => Assert.Same(loop, collection),
            collection => Assert.Same(first, collection),
            collection => Assert.Same(second, collection));
    }

    [Fact]
    public void OrderTestCollections_WhenLoopCollectionIsAbsent_PreservesDefaultOrder()
    {
        ITestCollection first = new TestCollectionStub("first", new Guid("00000000-0000-0000-0000-000000000000"));
        ITestCollection second = new TestCollectionStub("second", new Guid("00000001-0000-0000-0000-000000000000"));
        var discoveryOrder = new[] { second, first };

        var defaultOrdered = new DefaultTestCollectionOrderer().OrderTestCollections(discoveryOrder).ToArray();
        var ordered = new LoopRuntimeIntegrationCollectionOrderer().OrderTestCollections(discoveryOrder).ToArray();

        Assert.Equal(defaultOrdered, ordered);
    }

    [Fact]
    public void AssemblyConfiguration_RegistersTheOrdererAndPreservesCollectionParallelizationBoundaries()
    {
        var root = FindRepositoryRoot();
        var assemblyInfo = File.ReadAllText(Path.Combine(root, "tests", "EmbodySense.Core.Startup.Tests", "AssemblyInfo.cs"));

        Assert.Contains("[assembly: TestCollectionOrderer(\"EmbodySense.Core.Startup.Tests.LoopRuntimeIntegrationCollectionOrderer\", \"EmbodySense.Core.Startup.Tests\")]", assemblyInfo, StringComparison.Ordinal);
        Assert.Contains("[assembly: CollectionBehavior(MaxParallelThreads = 2)]", assemblyInfo, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
