using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.Loops.Execution.Effects;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects.Models;

/// <summary>Returns the exact command operation and graph-registration registries created over one native host.</summary>
/// <param name="Operations">The canonical #338 actuator-operation registry.</param>
/// <param name="Registrations">The exact graph descriptor to command registration registry.</param>
/// <param name="NativeHost">The shared native host used for exact current catalog readiness and execution.</param>
public sealed record GovernedCommandActionComposition(
    GovernedActuatorOperationRegistry Operations,
    CommandActionRegistrationRegistry Registrations,
    ICommandActionNativeHost NativeHost);
