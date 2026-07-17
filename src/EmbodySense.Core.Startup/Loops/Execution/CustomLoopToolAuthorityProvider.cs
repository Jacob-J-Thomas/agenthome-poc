using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class CustomLoopToolAuthorityProvider : ICustomLoopToolAuthorityProvider
{
    private static readonly CustomLoopToolAssignment[] Catalog = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search];
    private readonly LoopDefinitionStore _definitionStore;
    private readonly TimeProvider _timeProvider;

    public CustomLoopToolAuthorityProvider(LoopDefinitionStore definitionStore, TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(admittedMaximum);
        var evaluatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        LoopDefinition definition;
        try
        {
            definition = await _definitionStore.LoadAsync("default-conversation", cancellationToken) ?? LoopDefinition.CreateDefaultConversation();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid(roleId, admittedMaximum, evaluatedAtUtc, $"The current directory-role authority could not be loaded safely: {exception.GetType().Name}.");
        }

        var current = ResolveCurrentRoleCeiling(definition);
        var admitted = admittedMaximum.ToArray();
        var roleMatches = string.Equals(definition.RoleId, roleId, StringComparison.Ordinal);
        var assignmentsValid = admitted.All(Catalog.Contains) && admitted.Distinct().Count() == admitted.Length;
        var effective = roleMatches && assignmentsValid ? admitted.Intersect(current).OrderBy(value => value).ToArray() : [];
        var detail = !roleMatches
            ? "The admitted run role no longer matches the current server-owned directory role."
            : !assignmentsValid
                ? "The admitted command maximum contains an unsupported or duplicate assignment."
                : "Effective authority is the immutable admitted maximum intersected with the current directory-role ceiling and implemented catalog.";
        return new CustomLoopToolAuthoritySnapshot(
            definition.RoleId,
            admitted,
            current,
            Catalog.ToArray(),
            effective,
            ComputeRoleCeilingHash(definition.RoleId, current),
            ComputeCatalogHash(),
            evaluatedAtUtc,
            roleMatches && assignmentsValid,
            detail);
    }

    public static CustomLoopToolAssignment[] ResolveCurrentRoleCeiling(LoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Catalog
            .Where(assignment => LoopCapabilityIds.AllowsWorkspaceCommand(definition.CapabilityIds, MapCommand(assignment)))
            .OrderBy(value => value)
            .ToArray();
    }

    public static string ComputeRoleCeilingHash(string roleId, IReadOnlyList<CustomLoopToolAssignment> assignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(assignments);
        var canonical = roleId + "\n" + string.Join('\n', assignments.Distinct().OrderBy(value => value).Select(value => value.ToString().ToLowerInvariant()));
        return CustomLoopTraceContentHash.Compute(canonical);
    }

    public static string ComputeCatalogHash()
    {
        return CustomLoopTraceContentHash.Compute(string.Join('\n', Catalog.Select(value => value.ToString().ToLowerInvariant())));
    }

    private static CustomLoopToolAuthoritySnapshot Invalid(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, DateTimeOffset evaluatedAtUtc, string detail)
    {
        return new CustomLoopToolAuthoritySnapshot(
            roleId,
            admittedMaximum.ToArray(),
            [],
            Catalog.ToArray(),
            [],
            ComputeRoleCeilingHash(roleId, []),
            ComputeCatalogHash(),
            evaluatedAtUtc,
            false,
            detail);
    }

    private static ToolCommand MapCommand(CustomLoopToolAssignment assignment)
    {
        return assignment switch
        {
            CustomLoopToolAssignment.List => ToolCommand.List,
            CustomLoopToolAssignment.Read => ToolCommand.Read,
            CustomLoopToolAssignment.Search => ToolCommand.Search,
            _ => throw new ArgumentOutOfRangeException(nameof(assignment), assignment, "Only list, read, and search belong to the wave-one catalog.")
        };
    }
}
