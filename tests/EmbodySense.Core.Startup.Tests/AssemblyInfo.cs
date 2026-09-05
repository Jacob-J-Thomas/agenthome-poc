using Xunit;

[assembly: TestCollectionOrderer("EmbodySense.Core.Startup.Tests.LoopRuntimeIntegrationCollectionOrderer", "EmbodySense.Core.Startup.Tests")]
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
