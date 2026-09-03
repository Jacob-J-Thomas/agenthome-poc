using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationProbeRegistry : IGovernedLoopEffectReconciliationProbeRegistry
{
    private const string CursorPrefix = "reconciliation-probe-cursor-v1-";
    private readonly IReadOnlyDictionary<string, GovernedLoopEffectReconciliationProbeRegistration> _registrations;
    private readonly IReadOnlyList<GovernedLoopEffectReconciliationProbeRegistration> _ordered;

    private GovernedLoopEffectReconciliationProbeRegistry(IEnumerable<GovernedLoopEffectReconciliationProbeRegistration> registrations)
    {
        var captured = registrations.Take(257).ToArray();
        if (captured.Length > 256 || captured.Any(value => value?.Contract is null || value.Probe is null || !GovernedLoopEffectReconciliationContractValidator.Validate(value.Contract).IsValid))
        {
            throw new ArgumentException("The reconciliation probe registry is malformed or exceeds its finite bound.", nameof(registrations));
        }
        var map = new Dictionary<string, GovernedLoopEffectReconciliationProbeRegistration>(StringComparer.Ordinal);
        foreach (var registration in captured)
        {
            if (!map.TryAdd(registration.Contract.ContractId, registration))
            {
                throw new ArgumentException("The reconciliation probe registry contains a duplicate contract identity.", nameof(registrations));
            }
        }
        _registrations = new ReadOnlyDictionary<string, GovernedLoopEffectReconciliationProbeRegistration>(map);
        _ordered = Array.AsReadOnly(captured.OrderBy(value => value.Contract.ContractId, StringComparer.Ordinal).ThenBy(value => value.Contract.ContractVersion).ToArray());
    }

    internal static GovernedLoopEffectReconciliationProbeRegistry Create(
        GovernedActuatorOperationRegistry? operations,
        IGovernedLoopEffectReconciliationCaseStore cases,
        IGovernedLoopEffectReconciliationInputSource inputs,
        TimeProvider timeProvider)
        => Create([operations], cases, inputs, timeProvider);

    internal static GovernedLoopEffectReconciliationProbeRegistry Create(
        IEnumerable<GovernedActuatorOperationRegistry?> operationRegistries,
        IGovernedLoopEffectReconciliationCaseStore cases,
        IGovernedLoopEffectReconciliationInputSource inputs,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(operationRegistries);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var registrations = new List<GovernedLoopEffectReconciliationProbeRegistration>();
        foreach (var operations in operationRegistries.Where(value => value is not null))
        {
            foreach (var descriptor in operations!.Descriptors)
            {
                if (!operations.TryResolve(descriptor, out var operation) || operation is not IGovernedActuatorOutcomeProbe outcomeProbe)
                {
                    continue;
                }
                var metadata = CreateMetadata(descriptor);
                registrations.Add(new GovernedLoopEffectReconciliationProbeRegistration(metadata, new GovernedLoopEffectReconciliationCommandProbe(metadata, descriptor, outcomeProbe, cases, inputs, timeProvider)));
            }
        }
        return new GovernedLoopEffectReconciliationProbeRegistry(registrations);
    }

    internal bool TryResolve(GovernedLoopEffectAttempt? attempt, out GovernedLoopEffectReconciliationContractMetadata? metadata)
    {
        metadata = null;
        if (attempt is null)
        {
            return false;
        }

        var matches = _ordered.Where(value => Equals(value.Contract.Capability, attempt.Capability)
            && Equals(value.Contract.Implementation, attempt.Implementation)
            && string.Equals(value.Contract.ActuatorOperationId, attempt.ActuatorOperationId, StringComparison.Ordinal)
            && string.Equals(value.Contract.OperationDescriptorHash, attempt.OperationDescriptorHash, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        metadata = GovernedLoopEffectReconciliationContractCopy.Copy(matches[0].Contract);
        return true;
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryPage> ListAsync(GovernedLoopEffectReconciliationProbeRegistryListRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadOffset(request.Cursor, out var offset) || offset > _ordered.Count)
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Invalid, [], null));
        }

        var values = _ordered.Skip(offset).Take(request.MaximumCount).Select(value => value.Contract).ToArray();
        var nextOffset = offset + values.Length;
        var nextCursor = nextOffset < _ordered.Count ? CursorPrefix + nextOffset.ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, values, nextCursor));
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryReadResult> ReadAsync(GovernedLoopEffectReconciliationProbeRegistryReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registrations.TryGetValue(request.Contract.ContractId, out var registration))
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.NotFound, null, null));
        }
        return Task.FromResult(Equals(registration.Contract, request.Contract)
            ? new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, registration.Contract, registration.Probe)
            : new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict, registration.Contract, null));
    }

    private static GovernedLoopEffectReconciliationContractMetadata CreateMetadata(GovernedActuatorOperationDescriptor descriptor)
    {
        var discriminator = descriptor.ContentHash[..32];
        var probeHash = Hash("probe-contract", descriptor.ContentHash, descriptor.Capability.Hash.Value, descriptor.OperationId);
        return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "actuator-reconciliation-" + discriminator,
            1,
            descriptor.Capability,
            descriptor.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "actuator-outcome-probe-" + discriminator,
            1,
            probeHash,
            string.Empty));
    }

    private static bool TryReadOffset(string? cursor, out int offset)
    {
        offset = 0;
        if (cursor is null)
        {
            return true;
        }
        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal)
            || !int.TryParse(cursor.AsSpan(CursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || offset <= 0
            || !string.Equals(cursor, CursorPrefix + offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            offset = 0;
            return false;
        }
        return true;
    }

    private static string Hash(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.actuator-reconciliation.v1\n").Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
