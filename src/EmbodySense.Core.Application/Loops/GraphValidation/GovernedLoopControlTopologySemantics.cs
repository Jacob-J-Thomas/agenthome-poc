using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

internal static class GovernedLoopControlTopologySemantics
{
    internal static bool AreAllJoinInputsJointlySatisfiable(
        GovernedLoopGraphDefinition graph,
        string joinNodeId,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> incoming)
    {
        if (incoming.Any(edge => string.Equals(edge.FromNodeId, joinNodeId, StringComparison.Ordinal))
            || incoming.GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal).Any(group =>
                group.Select(edge => edge.Condition).Distinct().Any(first =>
                    group.Any(second => AreMutuallyExclusive(first, second.Condition)))))
        {
            return false;
        }

        var adjacency = graph.Nodes.ToDictionary(
            node => node.Id,
            _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges)
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }

        foreach (var branch in graph.ControlEdges
                     .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
                     .Where(group => !string.Equals(group.Key, joinNodeId, StringComparison.Ordinal)
                         && group.Select(edge => edge.Condition).Distinct().Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            foreach (var left in incoming)
            {
                var leftOutcomes = branch
                    .Where(edge => CanReachBeforeJoin(edge.ToNodeId, left.FromNodeId, joinNodeId, adjacency))
                    .Select(edge => edge.Condition)
                    .Distinct()
                    .ToArray();
                if (leftOutcomes.Length == 0)
                {
                    continue;
                }

                foreach (var right in incoming.Where(edge => string.CompareOrdinal(edge.Id, left.Id) > 0))
                {
                    var rightOutcomes = branch
                        .Where(edge => CanReachBeforeJoin(edge.ToNodeId, right.FromNodeId, joinNodeId, adjacency))
                        .Select(edge => edge.Condition)
                        .Distinct()
                        .ToArray();
                    if (rightOutcomes.Length > 0
                        && leftOutcomes.All(leftOutcome => rightOutcomes.All(rightOutcome => AreMutuallyExclusive(leftOutcome, rightOutcome))))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    internal static bool AreMutuallyExclusive(
        GovernedLoopControlCondition left,
        GovernedLoopControlCondition right)
    {
        return left != right && (IsTimeoutPair(left, right) || (left, right) is
            (GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure) or
            (GovernedLoopControlCondition.Failure, GovernedLoopControlCondition.Success) or
            (GovernedLoopControlCondition.True, GovernedLoopControlCondition.False) or
            (GovernedLoopControlCondition.False, GovernedLoopControlCondition.True) or
            (GovernedLoopControlCondition.Approved, GovernedLoopControlCondition.Rejected) or
            (GovernedLoopControlCondition.Rejected, GovernedLoopControlCondition.Approved));
    }

    private static bool IsTimeoutPair(
        GovernedLoopControlCondition left,
        GovernedLoopControlCondition right)
    {
        return left == GovernedLoopControlCondition.Timeout && IsExclusiveWithTimeout(right)
            || right == GovernedLoopControlCondition.Timeout && IsExclusiveWithTimeout(left);
    }

    private static bool IsExclusiveWithTimeout(GovernedLoopControlCondition condition)
        => condition is GovernedLoopControlCondition.Success
            or GovernedLoopControlCondition.Failure
            or GovernedLoopControlCondition.True
            or GovernedLoopControlCondition.False
            or GovernedLoopControlCondition.Approved
            or GovernedLoopControlCondition.Rejected;

    private static bool CanReachBeforeJoin(
        string source,
        string target,
        string joinNodeId,
        IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (string.Equals(current, joinNodeId, StringComparison.Ordinal) || !visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, target, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var next in adjacency[current].Reverse())
            {
                pending.Push(next);
            }
        }

        return false;
    }
}
