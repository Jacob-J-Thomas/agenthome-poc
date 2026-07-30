using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Resolves fail-closed custom-loop tool authority from the immutable admitted maximum, the current
/// default-conversation role ceiling, and the implemented read-only catalog.
/// </summary>
public sealed class CustomLoopToolAuthorityProvider : ICustomLoopToolAuthorityProvider
{
    private static readonly CustomLoopToolAssignment[] _catalog = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search];
    private readonly LoopDefinitionStore _definitionStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an authority provider over the persisted system definition.
    /// </summary>
    /// <param name="definitionStore">The store for the current default-conversation authority definition.</param>
    /// <param name="timeProvider">The optional clock used to timestamp authority evidence.</param>
    public CustomLoopToolAuthorityProvider(LoopDefinitionStore definitionStore, TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Intersects the admitted maximum with current role capability and the implemented catalog.
    /// </summary>
    /// <param name="roleId">The immutable role identity captured at admission.</param>
    /// <param name="admittedMaximum">The immutable tool-assignment maximum captured at admission.</param>
    /// <param name="cancellationToken">The token used to cancel system-definition loading.</param>
    /// <returns>
    /// A task whose result contains the admitted, current, catalog, and effective assignments plus
    /// canonical hashes. Missing, unreadable, substituted, role-mismatched, duplicate, or unsupported
    /// authority produces an invalid snapshot with no effective assignments.
    /// </returns>
    public async Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(admittedMaximum);
        var evaluatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        LoopDefinition? definition;
        try
        {
            definition = await _definitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid(roleId, admittedMaximum, evaluatedAtUtc, $"The current directory-role authority could not be loaded safely: {exception.GetType().Name}.");
        }

        if (definition is null)
        {
            return Invalid(roleId, admittedMaximum, evaluatedAtUtc, "The current directory-role authority definition is missing; custom-loop execution failed closed.");
        }

        if (!string.Equals(definition.Id, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal))
        {
            return Invalid(roleId, admittedMaximum, evaluatedAtUtc, "The current directory-role authority definition does not have the expected default-conversation identity; custom-loop execution failed closed.");
        }

        var current = ResolveCurrentRoleCeiling(definition);
        var admitted = admittedMaximum.ToArray();
        var roleMatches = string.Equals(definition.RoleId, roleId, StringComparison.Ordinal);
        var assignmentsValid = admitted.All(_catalog.Contains) && admitted.Distinct().Count() == admitted.Length;
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
            _catalog.ToArray(),
            effective,
            ComputeRoleCeilingHash(definition.RoleId, current),
            ComputeCatalogHash(),
            evaluatedAtUtc,
            roleMatches && assignmentsValid,
            detail);
    }

    /// <summary>
    /// Derives the currently implemented read-only assignments allowed by a system definition's capabilities.
    /// </summary>
    /// <param name="definition">The authoritative default-conversation definition.</param>
    /// <returns>Sorted list, read, and search assignments allowed by the definition.</returns>
    public static CustomLoopToolAssignment[] ResolveCurrentRoleCeiling(LoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _catalog
            .Where(assignment => LoopCapabilityIds.AllowsWorkspaceCommand(definition.CapabilityIds, MapCommand(assignment)))
            .OrderBy(value => value)
            .ToArray();
    }

    /// <summary>
    /// Computes the canonical content hash for a role identity and normalized assignment set.
    /// </summary>
    /// <param name="roleId">The nonblank role identity.</param>
    /// <param name="assignments">Assignments canonicalized by distinct value and enum order.</param>
    /// <returns>The SHA-256 trace-content hash of the canonical role ceiling.</returns>
    public static string ComputeRoleCeilingHash(string roleId, IReadOnlyList<CustomLoopToolAssignment> assignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(assignments);
        var canonical = roleId + "\n" + string.Join('\n', assignments.Distinct().OrderBy(value => value).Select(value => value.ToString().ToLowerInvariant()));
        return CustomLoopTraceContentHash.Compute(canonical);
    }

    /// <summary>
    /// Computes the canonical hash of the implemented custom-loop tool catalog.
    /// </summary>
    /// <returns>The stable SHA-256 trace-content hash for the ordered list, read, and search catalog.</returns>
    public static string ComputeCatalogHash()
    {
        return CustomLoopTraceContentHash.Compute(string.Join('\n', _catalog.Select(value => value.ToString().ToLowerInvariant())));
    }

    private static CustomLoopToolAuthoritySnapshot Invalid(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, DateTimeOffset evaluatedAtUtc, string detail)
    {
        return new CustomLoopToolAuthoritySnapshot(
            roleId,
            admittedMaximum.ToArray(),
            [],
            _catalog.ToArray(),
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
