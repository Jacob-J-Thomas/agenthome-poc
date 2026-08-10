using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Contains reusable unbound lifecycle evidence for one governed run.</summary>
/// <remarks>Construction validates lifecycle timestamps, terminality, and optimistic version bounds.</remarks>
public sealed record GovernedLoopRunLifecyclePayload
{
    private GovernedLoopRunLifecyclePayload(int schemaVersion, long lifecycleVersion, GovernedLoopRunStatus status, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, DateTimeOffset? terminalAtUtc)
    {
        SchemaVersion = schemaVersion;
        LifecycleVersion = lifecycleVersion;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        TerminalAtUtc = terminalAtUtc;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the positive optimistic lifecycle version.</summary>
    public long LifecycleVersion { get; }

    /// <summary>Gets the executor-neutral lifecycle status.</summary>
    public GovernedLoopRunStatus Status { get; }

    /// <summary>Gets the immutable UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the UTC timestamp of this lifecycle version.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets the UTC terminal timestamp, which equals <see cref="UpdatedAtUtc"/> on a terminal version.</summary>
    public DateTimeOffset? TerminalAtUtc { get; }

    /// <summary>Creates validated reusable unbound lifecycle evidence.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="lifecycleVersion">The positive bounded optimistic version.</param>
    /// <param name="status">The supported lifecycle status.</param>
    /// <param name="createdAtUtc">The immutable UTC creation timestamp.</param>
    /// <param name="updatedAtUtc">The UTC timestamp of this version.</param>
    /// <param name="terminalAtUtc">The terminal timestamp for a terminal version.</param>
    /// <returns>The validated lifecycle evidence.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="lifecycleVersion"/> is outside the supported positive range.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, status, timestamps, or terminal-state shape is invalid.</exception>
    public static GovernedLoopRunLifecyclePayload Create(int schemaVersion, long lifecycleVersion, GovernedLoopRunStatus status, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, DateTimeOffset? terminalAtUtc)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (!GovernedLoopExecutionStateMatrix.IsSupported(status))
        {
            throw new ArgumentException("A supported governed-loop run status is required.", nameof(status));
        }

        var created = GovernedLoopExecutionContractGuard.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        var updated = GovernedLoopExecutionContractGuard.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (created > updated)
        {
            throw new ArgumentException("The lifecycle update timestamp cannot precede creation.", nameof(updatedAtUtc));
        }

        DateTimeOffset? terminal = terminalAtUtc is null ? null : GovernedLoopExecutionContractGuard.RequireUtc(terminalAtUtc.Value, nameof(terminalAtUtc));
        if (GovernedLoopExecutionStateMatrix.IsTerminal(status) != (terminal is not null) || terminal is not null && terminal != updated)
        {
            throw new ArgumentException("Terminal lifecycle versions require one terminal timestamp equal to the update timestamp; nonterminal versions forbid it.", nameof(terminalAtUtc));
        }

        return new GovernedLoopRunLifecyclePayload(schemaVersion, GovernedLoopExecutionContractGuard.RequirePositiveVersion(lifecycleVersion, nameof(lifecycleVersion)), status, created, updated, terminal);
    }
}
