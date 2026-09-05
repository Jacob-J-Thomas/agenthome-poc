using System.Reflection;
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
    public void AssemblyConfiguration_PreservesCollectionParallelizationBoundaries()
    {
        var testAssembly = typeof(LoopRuntimeIntegrationCollectionOrderer).Assembly;
        var collectionBehavior = testAssembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        var loopCollection = typeof(LoopRuntimeIntegrationCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        var processEnvironmentCollection = typeof(ProcessEnvironmentCollection).GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(collectionBehavior);
        Assert.Equal(2, collectionBehavior.MaxParallelThreads);
        Assert.NotNull(loopCollection);
        Assert.False(loopCollection.DisableParallelization);
        Assert.NotNull(processEnvironmentCollection);
        Assert.True(processEnvironmentCollection.DisableParallelization);
        Assert.Single(testAssembly.GetCustomAttributes<TestCollectionOrdererAttribute>());
    }
}
