using EmbodySense.Core.Application.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns one fail-closed canonical Human Input binding-source observation.</summary>
/// <param name="Status">The closed source-read disposition.</param>
/// <param name="Binding">The exact ephemeral binding only when <paramref name="Status"/> is <see cref="GovernedLoopSequentialHumanInputBindingReadStatus.Ready"/>.</param>
public sealed record GovernedLoopSequentialHumanInputBindingReadResult(
    GovernedLoopSequentialHumanInputBindingReadStatus Status,
    GovernedLoopSequentialHumanInputBinding? Binding);
