using EmbodySense.Core.Startup.Runtime;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

public sealed class GovernedLoopEffectAttemptCompositionTests
{
    [Fact]
    public void Composition_and_its_mutable_attempt_port_are_not_exported_from_startup()
    {
        var exported = typeof(AgentRuntimeFactory).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type => type.Name == "GovernedLoopEffectAttemptComposition");
    }
}
