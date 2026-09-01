using EmbodySense.Core.Startup.Tests.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Runtime;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class AgentRuntimeFactoryInvocationAuthorityTests
{
    [Fact]
    public Task CreateAsync_default_conversation_revalidates_current_authority_before_a_tool_actuation() => AgentRuntimeFactoryTests.CreateAsync_default_conversation_revalidates_current_authority_before_a_tool_actuation();

    [Fact]
    public Task CreateAsync_default_conversation_denies_tool_actuation_when_definition_authority_narrows() => AgentRuntimeFactoryTests.CreateAsync_default_conversation_denies_tool_actuation_when_definition_authority_narrows();
}
