using EmbodySense.Core.Startup.Tests.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Runtime;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class AgentRuntimeFactoryNestedProcessTests
{
    [Fact]
    public Task CreateAsync_exposes_authoring_that_observes_the_runtime_materialized_nonterminal_run_until_runtime_disposal() => AgentRuntimeFactoryTests.CreateAsync_exposes_authoring_that_observes_the_runtime_materialized_nonterminal_run_until_runtime_disposal();
}
