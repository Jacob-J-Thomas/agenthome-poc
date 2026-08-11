using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.ContextualRoles;

namespace EmbodySense.Core.Common.Loops.Execution;

internal static class GovernedLoopExecutionContractGuard
{
    internal static void RequireSchema(int schemaVersion, string parameterName)
    {
        if (schemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            throw new ArgumentException($"Governed-loop execution schema version must be {GovernedLoopExecutionLimits.CurrentSchemaVersion}.", parameterName);
        }
    }

    internal static string RequireIdentifier(string? value, string parameterName, int maxCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, maxCharacters))
        {
            throw new ArgumentException("Governed-loop execution identifiers must be bounded canonical lowercase tokens.", parameterName);
        }

        return value!;
    }

    internal static string? RequireOptionalIdentifier(string? value, string parameterName, int maxCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters)
    {
        return value is null ? null : RequireIdentifier(value, parameterName, maxCharacters);
    }

    internal static string RequireSha256(string? value, string parameterName)
    {
        if (value?.Length != GovernedLoopExecutionLimits.Sha256HexCharacters || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Governed-loop execution hashes must be canonical lowercase SHA-256 values.", parameterName);
        }

        return value;
    }

    internal static string RequireWorkspaceId(string? value, string parameterName)
    {
        if (!ContextualRoleWorkspaceId.IsValid(value))
        {
            throw new ArgumentException("A canonical hash-derived workspace scope identifier is required.", parameterName);
        }

        return value!;
    }

    internal static long RequirePositiveVersion(long value, string parameterName, long maximum = GovernedLoopExecutionLimits.MaxVersion)
    {
        if (value is < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution versions must be positive and no greater than {maximum}.");
        }

        return value;
    }

    internal static int? RequireOptionalAttempt(int? value, string parameterName)
    {
        if (value is < 1 || value > GovernedLoopExecutionLimits.MaxNodeAttempt)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop node attempts must be positive and no greater than {GovernedLoopExecutionLimits.MaxNodeAttempt}.");
        }

        return value;
    }

    internal static int RequirePlanOrdinal(int value, string parameterName)
    {
        if (value is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop plan ordinals must be between 0 and {GovernedLoopExecutionLimits.MaxFrontierNodes - 1}.");
        }

        return value;
    }

    internal static int RequireActivationOrdinal(int value, string parameterName)
    {
        if (value is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop activation ordinals must be between 0 and {GovernedLoopExecutionLimits.MaxFrontierNodes - 1}.");
        }

        return value;
    }

    internal static int RequireVisitOrdinal(int value, string parameterName)
    {
        if (value is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop node visits must be positive and no greater than {GovernedLoopExecutionLimits.MaxNodeVisits}.");
        }

        return value;
    }

    internal static int? RequireOptionalCycleIteration(int? value, string parameterName)
    {
        if (value is < 1 or > GovernedLoopExecutionLimits.MaxCycleIterations)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop cycle iterations must be positive and no greater than {GovernedLoopExecutionLimits.MaxCycleIterations}.");
        }

        return value;
    }

    internal static GovernedLoopNodeDescriptor RequireNodeDescriptor(GovernedLoopNodeDescriptor? descriptor, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(descriptor, parameterName);
        if (descriptor.Kind == GovernedLoopNodeKind.Unknown || !Enum.IsDefined(descriptor.Kind))
        {
            throw new ArgumentException("A supported governed-loop node kind is required.", parameterName);
        }

        RequireIdentifier(descriptor.TypeId, parameterName);
        RequirePositiveVersion(descriptor.Version, parameterName);
        return descriptor;
    }

    internal static void RequireCanonicalActivationHistory(IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes, string parameterName)
    {
        var planNodes = new Dictionary<int, GovernedLoopNodeExecutionEvidence>();
        var nodePlans = new Dictionary<string, GovernedLoopNodeExecutionEvidence>(StringComparer.Ordinal);
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastCycleIterations = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.ActivationOrdinal != index)
            {
                throw new ArgumentException("Governed-loop frontier activations must form a contiguous zero-based activation history.", parameterName);
            }

            var expectedVisit = visits.TryGetValue(node.NodeId, out var priorVisits) ? priorVisits + 1 : 1;
            if (node.VisitOrdinal != expectedVisit)
            {
                throw new ArgumentException("Governed-loop activation visit ordinals must be contiguous for each exact node identity.", parameterName);
            }

            visits[node.NodeId] = expectedVisit;
            if (nodePlans.TryGetValue(node.NodeId, out var priorNode)
                && (priorNode.PlanOrdinal != node.PlanOrdinal
                    || priorNode.Descriptor != node.Descriptor
                    || !priorNode.IncomingControlEdgeIds.SequenceEqual(node.IncomingControlEdgeIds, StringComparer.Ordinal)
                    || !priorNode.OutgoingControlEdgeIds.SequenceEqual(node.OutgoingControlEdgeIds, StringComparer.Ordinal)
                    || priorNode.CycleId is null
                    || node.CycleId is null
                    || !string.Equals(priorNode.CycleId, node.CycleId, StringComparison.Ordinal)))
            {
                throw new ArgumentException("Repeated governed-loop activations must retain one immutable admitted plan identity and topology.", parameterName);
            }

            if (planNodes.TryGetValue(node.PlanOrdinal, out var planned))
            {
                if (!string.Equals(planned.NodeId, node.NodeId, StringComparison.Ordinal)
                    || planned.Descriptor != node.Descriptor
                    || !planned.IncomingControlEdgeIds.SequenceEqual(node.IncomingControlEdgeIds, StringComparer.Ordinal)
                    || !planned.OutgoingControlEdgeIds.SequenceEqual(node.OutgoingControlEdgeIds, StringComparer.Ordinal))
                {
                    throw new ArgumentException("Repeated governed-loop activations must retain one immutable admitted plan identity and topology.", parameterName);
                }

                if (node.CycleId is null)
                {
                    throw new ArgumentException("A repeated governed-loop node activation requires explicit cycle identity and iteration evidence.", parameterName);
                }
            }
            else
            {
                planNodes.Add(node.PlanOrdinal, node);
                nodePlans.Add(node.NodeId, node);
            }

            if (node.CycleId is { } cycleId && node.CycleIteration is { } cycleIteration)
            {
                if (lastCycleIterations.TryGetValue(cycleId, out var lastCycleIteration) && cycleIteration < lastCycleIteration)
                {
                    throw new ArgumentException("Cycle iteration evidence cannot move backward within one exact cycle identity.", parameterName);
                }

                lastCycleIterations[cycleId] = cycleIteration;
            }

            foreach (var arrival in node.JoinArrivals)
            {
                if (arrival.SourceActivationOrdinal >= node.ActivationOrdinal)
                {
                    throw new ArgumentException("Join arrivals must identify an earlier exact source activation.", parameterName);
                }

                var source = nodes[arrival.SourceActivationOrdinal];
                if (!source.SelectedControlEdgeIds.Contains(arrival.ControlEdgeId, StringComparer.Ordinal))
                {
                    throw new ArgumentException("Join arrivals must identify a control edge selected by their exact source activation.", parameterName);
                }
            }
        }
    }

    internal static IReadOnlyList<GovernedLoopJoinArrivalEvidence> SnapshotJoinArrivals(IEnumerable<GovernedLoopJoinArrivalEvidence>? values, string parameterName)
    {
        var snapshot = SnapshotBounded(values, parameterName, GovernedLoopExecutionLimits.MaxJoinArrivals);
        string? previousEdgeId = null;
        foreach (var value in snapshot)
        {
            if (value.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
            {
                throw new ArgumentException("Governed-loop join arrivals must use schema version 1.", parameterName);
            }

            if (previousEdgeId is not null && string.CompareOrdinal(previousEdgeId, value.ControlEdgeId) >= 0)
            {
                throw new ArgumentException("Governed-loop join arrivals must be sorted and unique by control-edge identity.", parameterName);
            }

            previousEdgeId = value.ControlEdgeId;
        }

        return Array.AsReadOnly(snapshot.Select(value => GovernedLoopJoinArrivalEvidence.Create(value.SchemaVersion, value.ControlEdgeId, value.SourceActivationOrdinal)).ToArray());
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Governed-loop execution timestamps must be non-default UTC values with zero offset.", parameterName);
        }

        return value;
    }

    internal static IReadOnlyList<string> SnapshotSortedUniqueIdentifiers(IEnumerable<string>? values, string parameterName, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution identifier collections may contain at most {maximumCount} items.");
        }

        string? previous = null;
        foreach (var value in snapshot)
        {
            RequireIdentifier(value, parameterName);
            if (previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                throw new ArgumentException("Governed-loop execution identifier collections must be sorted and unique.", parameterName);
            }

            previous = value;
        }

        return Array.AsReadOnly(snapshot);
    }

    internal static IReadOnlyList<TValue> SnapshotBounded<TValue>(IEnumerable<TValue>? values, string parameterName, int maximumCount)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution evidence collections may contain at most {maximumCount} items.");
        }

        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Governed-loop execution evidence collections cannot contain null items.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
