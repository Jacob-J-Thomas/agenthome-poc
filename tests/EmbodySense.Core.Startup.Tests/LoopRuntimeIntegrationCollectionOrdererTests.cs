using System.Reflection;
using EmbodySense.Core.Startup.Tests.Loops.Execution;
using EmbodySense.Core.Startup.Tests.Verification;
using Xunit.Abstractions;

namespace EmbodySense.Core.Startup.Tests;

public sealed class LoopRuntimeIntegrationCollectionOrdererTests
{
    [Fact]
    public void OrderTestCollections_MovesLoopCollectionFirstAndPreservesEveryOtherCollectionOrder()
    {
        ITestCollection first = new TestCollectionStub("first");
        ITestCollection loop = new TestCollectionStub(LoopRuntimeIntegrationCollection.Name);
        ITestCollection second = new TestCollectionStub("second");
        ITestCollection third = new TestCollectionStub("third");

        var ordered = new LoopRuntimeIntegrationCollectionOrderer().OrderTestCollections([first, loop, second, third]).ToArray();

        Assert.Collection(
            ordered,
            collection => Assert.Same(loop, collection),
            collection => Assert.Same(first, collection),
            collection => Assert.Same(second, collection),
            collection => Assert.Same(third, collection));
    }

    [Fact]
    public void OrderTestCollections_WhenLoopCollectionIsAbsent_PreservesTheOriginalSequence()
    {
        ITestCollection first = new TestCollectionStub("first");
        ITestCollection second = new TestCollectionStub("second");

        var ordered = new LoopRuntimeIntegrationCollectionOrderer().OrderTestCollections([first, second]).ToArray();

        Assert.Collection(ordered, collection => Assert.Same(first, collection), collection => Assert.Same(second, collection));
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
