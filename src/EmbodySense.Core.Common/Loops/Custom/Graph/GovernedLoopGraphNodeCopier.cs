using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Custom.Graph;

internal static class GovernedLoopGraphNodeCopier
{
    internal static GovernedLoopNodeDefinition[] Copy(IEnumerable<GovernedLoopNodeDefinition> nodes)
        => nodes.Select(Copy).ToArray();

    private static GovernedLoopNodeDefinition Copy(GovernedLoopNodeDefinition node)
        => new(
            node.Id,
            node.Descriptor,
            node.Ports,
            node.AuthorityCeiling,
            node.Parameters,
            node.ModelRoutingPolicy,
            node.AuthoredInputDataClasses,
            node.RetryPolicy,
            GovernedLoopHumanInputNodeConfigurationSnapshot.Copy(node.HumanInputConfiguration));
}
