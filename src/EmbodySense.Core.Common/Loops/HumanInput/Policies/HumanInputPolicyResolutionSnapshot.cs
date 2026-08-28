using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Captures the exact trusted-time resolution of one timeout and one terminal-disposition policy for a governed Human Input request.</summary>
/// <param name="SchemaVersion">The resolution schema version, which must be 1.</param>
/// <param name="WorkspaceId">The server-derived workspace scope.</param>
/// <param name="GraphId">The exact governed-loop graph scope.</param>
/// <param name="GraphRevisionId">The immutable graph-revision identity.</param>
/// <param name="NodeId">The exact Human Input graph-node identity.</param>
/// <param name="ActorId">The server-derived actor requesting resolution.</param>
/// <param name="TimeoutPolicy">The exact immutable finite-window policy revision.</param>
/// <param name="FailurePolicy">The exact immutable deadline-disposition policy revision.</param>
/// <param name="ResolvedAtUtc">The trusted UTC instant at which the finite response window opened.</param>
/// <param name="ExpiresAtUtc">The trusted overflow-safe finite deadline derived from the exact timeout policy.</param>
/// <param name="TerminalDisposition">The one closed non-authorizing disposition reached at the deadline.</param>
/// <param name="ResolutionHash">The canonical hash over every resolution coordinate.</param>
public sealed record HumanInputPolicyResolutionSnapshot(
    int SchemaVersion,
    string WorkspaceId,
    string GraphId,
    string GraphRevisionId,
    string NodeId,
    string ActorId,
    HumanInputPolicyArtifact TimeoutPolicy,
    HumanInputPolicyArtifact FailurePolicy,
    DateTimeOffset ResolvedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    HumanInputTerminalDisposition TerminalDisposition,
    string ResolutionHash)
{
    private readonly HumanInputPolicyArtifact _timeoutPolicy = Copy(TimeoutPolicy);
    private readonly HumanInputPolicyArtifact _failurePolicy = Copy(FailurePolicy);

    /// <summary>Gets the only supported Human Input policy-resolution schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets an independent exact immutable timeout-policy revision.</summary>
    public HumanInputPolicyArtifact TimeoutPolicy => Copy(_timeoutPolicy);

    /// <summary>Gets an independent exact immutable failure-policy revision.</summary>
    public HumanInputPolicyArtifact FailurePolicy => Copy(_failurePolicy);

    /// <summary>Creates the canonical hash-bound snapshot from exact validated policy revisions and one trusted UTC instant.</summary>
    /// <param name="workspaceId">The server-derived workspace scope.</param>
    /// <param name="graphId">The exact graph scope.</param>
    /// <param name="graphRevisionId">The immutable graph revision.</param>
    /// <param name="nodeId">The exact Human Input node.</param>
    /// <param name="actorId">The server-derived resolution actor.</param>
    /// <param name="timeoutPolicy">The exact finite-window policy revision.</param>
    /// <param name="failurePolicy">The exact deadline-disposition policy revision.</param>
    /// <param name="resolvedAtUtc">The trusted UTC instant at which the window opens.</param>
    /// <returns>The exact complete snapshot, or null when any coordinate is invalid or the deadline overflows.</returns>
    public static HumanInputPolicyResolutionSnapshot? TryCreate(
        string? workspaceId,
        string? graphId,
        string? graphRevisionId,
        string? nodeId,
        string? actorId,
        HumanInputPolicyArtifact? timeoutPolicy,
        HumanInputPolicyArtifact? failurePolicy,
        DateTimeOffset resolvedAtUtc)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId)
            || !HumanInputIdentifier.IsValid(graphId)
            || !HumanInputIdentifier.IsValid(graphRevisionId)
            || !HumanInputIdentifier.IsValid(nodeId)
            || !HumanInputIdentifier.IsValid(actorId)
            || resolvedAtUtc == default
            || resolvedAtUtc.Offset != TimeSpan.Zero
            || !HumanInputPolicyArtifactValidator.Validate(timeoutPolicy).IsValid
            || !HumanInputPolicyArtifactValidator.Validate(failurePolicy).IsValid
            || timeoutPolicy!.Kind != HumanInputPolicyKind.ResponseWindow
            || failurePolicy!.Kind != HumanInputPolicyKind.DeadlineDisposition
            || timeoutPolicy.ResponseWindowMilliseconds is not { } window
            || failurePolicy.TerminalDisposition != HumanInputTerminalDisposition.Expired
            || !SameScope(timeoutPolicy, workspaceId!, graphId!, actorId!)
            || !SameScope(failurePolicy, workspaceId!, graphId!, actorId!))
        {
            return null;
        }

        DateTimeOffset expiry;
        try
        {
            expiry = resolvedAtUtc.AddMilliseconds(window);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        var snapshot = new HumanInputPolicyResolutionSnapshot(CurrentSchemaVersion, workspaceId!, graphId!, graphRevisionId!, nodeId!, actorId!, timeoutPolicy, failurePolicy, resolvedAtUtc, expiry, failurePolicy.TerminalDisposition, string.Empty);
        return snapshot with { ResolutionHash = ComputeHash(snapshot) };
    }

    /// <summary>Gets whether a snapshot retains exact valid policy revisions, trusted finite timing, scope, and canonical resolution hash.</summary>
    /// <param name="snapshot">The untrusted snapshot candidate.</param>
    /// <returns><see langword="true"/> only for an exact schema-1 snapshot.</returns>
    public static bool IsValid(HumanInputPolicyResolutionSnapshot? snapshot)
    {
        if (snapshot is null
            || snapshot.SchemaVersion != CurrentSchemaVersion
            || !ContextualRoleWorkspaceId.IsValid(snapshot.WorkspaceId)
            || !HumanInputIdentifier.IsValid(snapshot.GraphId)
            || !HumanInputIdentifier.IsValid(snapshot.GraphRevisionId)
            || !HumanInputIdentifier.IsValid(snapshot.NodeId)
            || !HumanInputIdentifier.IsValid(snapshot.ActorId)
            || snapshot.ResolvedAtUtc == default
            || snapshot.ExpiresAtUtc == default
            || snapshot.ResolvedAtUtc.Offset != TimeSpan.Zero
            || snapshot.ExpiresAtUtc.Offset != TimeSpan.Zero
            || snapshot.TerminalDisposition != HumanInputTerminalDisposition.Expired
            || !HumanInputPolicyArtifactValidator.Validate(snapshot.TimeoutPolicy).IsValid
            || !HumanInputPolicyArtifactValidator.Validate(snapshot.FailurePolicy).IsValid
            || snapshot.TimeoutPolicy.Kind != HumanInputPolicyKind.ResponseWindow
            || snapshot.FailurePolicy.Kind != HumanInputPolicyKind.DeadlineDisposition
            || snapshot.FailurePolicy.TerminalDisposition != snapshot.TerminalDisposition
            || snapshot.TimeoutPolicy.ResponseWindowMilliseconds is not { } window
            || !SameScope(snapshot.TimeoutPolicy, snapshot.WorkspaceId, snapshot.GraphId, snapshot.ActorId)
            || !SameScope(snapshot.FailurePolicy, snapshot.WorkspaceId, snapshot.GraphId, snapshot.ActorId)
            || !HumanInputPolicyArtifactHash.IsSha256(snapshot.ResolutionHash))
        {
            return false;
        }

        try
        {
            return snapshot.ExpiresAtUtc == snapshot.ResolvedAtUtc.AddMilliseconds(window)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(snapshot.ResolutionHash), Encoding.ASCII.GetBytes(ComputeHash(snapshot)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Computes the canonical snapshot hash without trusting its stored hash field.</summary>
    /// <param name="snapshot">The snapshot to hash.</param>
    /// <returns>The lowercase SHA-256 digest.</returns>
    public static string ComputeHash(HumanInputPolicyResolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var content = string.Join('\n', "embodysense-human-input-policy-resolution-v1", snapshot.SchemaVersion, snapshot.WorkspaceId ?? string.Empty, snapshot.GraphId ?? string.Empty, snapshot.GraphRevisionId ?? string.Empty, snapshot.NodeId ?? string.Empty, snapshot.ActorId ?? string.Empty, snapshot.TimeoutPolicy.ContentHash ?? string.Empty, snapshot.FailurePolicy.ContentHash ?? string.Empty, snapshot.ResolvedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), snapshot.ExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), (int)snapshot.TerminalDisposition);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static HumanInputPolicyArtifact Copy(HumanInputPolicyArtifact? artifact)
        => artifact is null
            ? null!
            : new HumanInputPolicyArtifact(artifact.SchemaVersion, artifact.PolicyId, artifact.RevisionId, artifact.Kind, artifact.WorkspaceId, artifact.GraphId, artifact.AuthorityActorId, artifact.ResponseWindowMilliseconds, artifact.TerminalDisposition, artifact.ContentHash);

    private static bool SameScope(HumanInputPolicyArtifact policy, string workspaceId, string graphId, string actorId)
        => string.Equals(policy.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && string.Equals(policy.GraphId, graphId, StringComparison.Ordinal)
            && string.Equals(policy.AuthorityActorId, actorId, StringComparison.Ordinal);
}
