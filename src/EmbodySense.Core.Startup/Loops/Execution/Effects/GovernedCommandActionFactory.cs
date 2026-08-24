using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.CommandActions;
using EmbodySense.Core.Startup.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Composes a finite immutable command-template catalog through canonical governed actuator operations.</summary>
public static class GovernedCommandActionFactory
{
    /// <summary>Creates an exact operation registry over one shared durable evidence store and registered native isolation adapter.</summary>
    public static GovernedActuatorOperationRegistry CreateRegistry(
        WorkspacePaths paths,
        IEnumerable<CommandActionRegistration> registrations,
        ICapabilityExecutableArtifactResolver artifactResolver,
        ICommandActionProcessIsolationBoundary isolationBoundary,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(artifactResolver);
        ArgumentNullException.ThrowIfNull(isolationBoundary);
        return Create(paths, registrations, artifactResolver, isolationBoundary, timeProvider).Operations;
    }

    /// <summary>Creates exact operation and graph-registration registries over one shared durable native host.</summary>
    public static GovernedCommandActionComposition Create(
        WorkspacePaths paths,
        IEnumerable<CommandActionRegistration> registrations,
        ICapabilityExecutableArtifactResolver artifactResolver,
        ICommandActionProcessIsolationBoundary isolationBoundary,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(artifactResolver);
        ArgumentNullException.ThrowIfNull(isolationBoundary);
        var registry = new CommandActionRegistrationRegistry(registrations);
        var evidence = new CommandActionEvidenceStore(paths);
        var concurrency = new CommandActionConcurrencyGate(paths);
        var host = new IsolatedCommandActionNativeHost(evidence, artifactResolver, isolationBoundary, concurrency, timeProvider);
        var operations = new GovernedActuatorOperationRegistry(registry.Registrations.Select(registration => new GovernedCommandActionOperation(registration, host)));
        return new GovernedCommandActionComposition(operations, registry, host);
    }
}
