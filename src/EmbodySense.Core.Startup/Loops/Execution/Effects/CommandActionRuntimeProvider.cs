using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Supplies one finite server-owned command-template, artifact-resolution, and isolation set to Startup.</summary>
public sealed class CommandActionRuntimeProvider
{
    /// <summary>Creates one replaceable command Action runtime provider.</summary>
    public CommandActionRuntimeProvider(
        IEnumerable<CommandActionRegistration> registrations,
        ICapabilityExecutableArtifactResolver artifactResolver,
        ICommandActionProcessIsolationBoundary isolationBoundary)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArtifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        IsolationBoundary = isolationBoundary ?? throw new ArgumentNullException(nameof(isolationBoundary));
        var captured = registrations.Take(257).ToArray();
        if (captured.Length > 256 || captured.Any(registration => CommandActionRegistrationContract.Validate(registration) is not null))
        {
            throw new ArgumentException("Choose no more than 256 valid command Action registrations.", nameof(registrations));
        }
        Registrations = Array.AsReadOnly(captured);
    }

    /// <summary>Gets the detached immutable command registration snapshot.</summary>
    public IReadOnlyList<CommandActionRegistration> Registrations { get; }

    /// <summary>Gets the exact activated-artifact lease resolver.</summary>
    public ICapabilityExecutableArtifactResolver ArtifactResolver { get; }

    /// <summary>Gets the registered pre-execution isolation boundary.</summary>
    public ICommandActionProcessIsolationBoundary IsolationBoundary { get; }

    /// <summary>Gets whether the registered platform boundary can enforce every declared control before launch.</summary>
    public bool IsIsolationAvailable(CommandActionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Registrations.Contains(registration)
            || registration.Template.RequiresCredentialChannel
            || registration.Template.Slots.Any(slot => slot.Kind == CommandActionSlotKind.WorkspaceRelativeTarget))
        {
            return false;
        }
        try
        {
            return IsolationBoundary.CheckAvailability(registration).Status == EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Available;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
