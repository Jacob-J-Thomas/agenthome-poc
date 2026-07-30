using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Chooses whether a custom-loop node inherits the definition defaults or uses an explicit context policy.
/// </summary>
/// <param name="Mode">The mode.</param>
/// <param name="CustomPolicy">The custom policy.</param>
public sealed record CustomLoopNodeContextPolicy(
    CustomLoopContextPolicyMode Mode,
    CustomLoopContextPolicy? CustomPolicy)
{
    /// <summary>
    /// Creates a policy that inherits the definition-level defaults.
    /// </summary>
    /// <returns>The custom loop node context policy.</returns>
    public static CustomLoopNodeContextPolicy Inherit()
    {
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Inherit, null);
    }

    /// <summary>
    /// Creates a policy that uses an explicit node-level context override.
    /// </summary>
    /// <param name="policy">The explicit context input/output policy.</param>
    /// <returns>A custom-mode node policy carrying the supplied override.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is <see langword="null"/>.</exception>
    public static CustomLoopNodeContextPolicy Override(CustomLoopContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, policy);
    }
}
