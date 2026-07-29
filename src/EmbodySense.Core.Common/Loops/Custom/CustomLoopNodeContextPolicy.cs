using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

public sealed record CustomLoopNodeContextPolicy(
    CustomLoopContextPolicyMode Mode,
    CustomLoopContextPolicy? CustomPolicy)
{
    public static CustomLoopNodeContextPolicy Inherit()
    {
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Inherit, null);
    }

    public static CustomLoopNodeContextPolicy Override(CustomLoopContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, policy);
    }
}
