using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace EmbodySense.Core.Startup.Tests;

/// <summary>
/// Applies xUnit's default collection order before submitting the Loop runtime integration collection first.
/// </summary>
/// <remarks>
/// xUnit uses this sequence as a submission hint. Parallel collection admission remains governed by the runner's
/// semaphore and is not guaranteed by this orderer.
/// </remarks>
public sealed class LoopRuntimeIntegrationCollectionOrderer : ITestCollectionOrderer
{
    /// <inheritdoc />
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        ArgumentNullException.ThrowIfNull(testCollections);

        var collections = new DefaultTestCollectionOrderer().OrderTestCollections(testCollections).ToList();
        var loopCollectionIndex = collections.FindIndex(collection => collection.DisplayName == Loops.Execution.LoopRuntimeIntegrationCollection.Name);

        if (loopCollectionIndex <= 0)
        {
            return collections;
        }

        return [collections[loopCollectionIndex], .. collections.Take(loopCollectionIndex), .. collections.Skip(loopCollectionIndex + 1)];
    }
}
