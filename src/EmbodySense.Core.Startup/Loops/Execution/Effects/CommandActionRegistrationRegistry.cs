using System.Collections.ObjectModel;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Owns one finite immutable command-template set keyed by exact graph descriptor hash.</summary>
public sealed class CommandActionRegistrationRegistry : ICommandActionRegistrationResolver
{
    private readonly IReadOnlyDictionary<string, CommandActionRegistration> _registrations;

    /// <summary>Creates a duplicate-rejecting immutable registration snapshot.</summary>
    public CommandActionRegistrationRegistry(IEnumerable<CommandActionRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var captured = registrations.Take(257).ToArray();
        if (captured.Length > 256 || captured.Any(registration => CommandActionRegistrationContract.Validate(registration) is not null))
        {
            throw new ArgumentException("The finite command registration catalog is malformed or too large.", nameof(registrations));
        }
        var map = new Dictionary<string, CommandActionRegistration>(StringComparer.Ordinal);
        foreach (var registration in captured)
        {
            var key = CommandActionNodeDescriptors.For(registration.Template).TypeId;
            if (!map.TryAdd(key, registration))
            {
                throw new ArgumentException("The finite command registration catalog contains duplicate exact templates.", nameof(registrations));
            }
        }
        _registrations = new ReadOnlyDictionary<string, CommandActionRegistration>(map);
        Registrations = Array.AsReadOnly(map.Values.OrderBy(item => item.Template.TemplateId, StringComparer.Ordinal).ThenBy(item => item.Template.ContentHash, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the detached ordered immutable registration snapshot.</summary>
    public IReadOnlyList<CommandActionRegistration> Registrations { get; }

    /// <inheritdoc />
    public bool TryResolve(GovernedLoopNodeDescriptor descriptor, out CommandActionRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        registration = null;
        return CommandActionNodeDescriptors.IsCommandAction(descriptor)
            && _registrations.TryGetValue(descriptor.TypeId, out registration)
            && CommandActionNodeDescriptors.Matches(descriptor, registration.Template);
    }
}
