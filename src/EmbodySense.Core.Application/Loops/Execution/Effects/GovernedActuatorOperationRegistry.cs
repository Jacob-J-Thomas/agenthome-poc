using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Captures one finite immutable server-owned actuator registry.</summary>
public sealed class GovernedActuatorOperationRegistry : IGovernedActuatorOperationRegistry
{
    private const int MaximumOperations = 256;
    private readonly IReadOnlyDictionary<string, GovernedActuatorOperationDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<string, IGovernedActuatorOperation> _operations;

    /// <summary>Creates an immutable registry and rejects duplicate or malformed registrations.</summary>
    public GovernedActuatorOperationRegistry(IEnumerable<IGovernedActuatorOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var captured = operations.Take(MaximumOperations + 1).ToArray();
        if (captured.Length > MaximumOperations || captured.Any(operation => operation is null))
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "The server actuator registry exceeded its finite bound or contained a null registration.");
        }

        var map = new Dictionary<string, IGovernedActuatorOperation>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, GovernedActuatorOperationDescriptor>(StringComparer.Ordinal);
        foreach (var operation in captured)
        {
            var descriptor = operation.Descriptor;
            var error = GovernedActuatorOperationContract.Validate(descriptor);
            if (error is not null)
            {
                throw new ArgumentException(error, nameof(operations));
            }
            var key = Key(descriptor);
            if (!map.TryAdd(key, operation) || !descriptors.TryAdd(key, descriptor with { }))
            {
                throw new ArgumentException("Duplicate exact server actuator registration.", nameof(operations));
            }
        }

        _operations = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IGovernedActuatorOperation>(map);
        _descriptors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, GovernedActuatorOperationDescriptor>(descriptors);
        Descriptors = Array.AsReadOnly(descriptors.Values
            .OrderBy(descriptor => descriptor.Capability.Id.Value, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray());
    }

    /// <inheritdoc />
    public IReadOnlyList<GovernedActuatorOperationDescriptor> Descriptors { get; }

    /// <inheritdoc />
    public bool TryResolve(GovernedActuatorOperationDescriptor descriptor, out IGovernedActuatorOperation? operation)
    {
        operation = null;
        if (GovernedActuatorOperationContract.Validate(descriptor) is not null
            || !_operations.TryGetValue(Key(descriptor), out var registered)
            || !_descriptors.TryGetValue(Key(descriptor), out var registeredDescriptor)
            || !Equals(registeredDescriptor, descriptor))
        {
            return false;
        }
        operation = registered;
        return true;
    }

    private static string Key(GovernedActuatorOperationDescriptor descriptor)
        => $"{descriptor.Capability.Id.Value}\u001f{descriptor.Capability.Version.Value}\u001f{descriptor.Capability.Hash.Value}\u001f{descriptor.OperationId}";
}
