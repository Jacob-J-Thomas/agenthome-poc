using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>Creates the deterministic default contextual-role declaration for a newly initialized workspace.</summary>
/// <remarks>
/// The declaration is an applicability-bound policy ceiling only. It does not create an authority grant,
/// provider profile, consent record, credential, or admission decision.
/// </remarks>
public sealed class DefaultContextualRoleSeeder : IDefaultContextualRoleSeeder
{
    /// <summary>Gets the stable identity of the built-in default role.</summary>
    public const string RoleId = "default-assistant";

    /// <summary>Gets the first and only revision created by fresh-workspace initialization.</summary>
    public const int Revision = 1;

    private const string ActorId = "embodysense-initializer";
    private const string OperationId = "seed-default-assistant-v1";
    private const string InstructionReferenceId = "role";
    private static readonly DateTimeOffset _seedTimestamp = DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public async Task<ContextualRoleRevisionPin> SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new InvalidOperationException("The server-derived workspace identity is outside the contextual-role contract.");
        }

        var expected = CreateRevision(workspaceId);
        var source = await new WorkspaceContextualRoleInstructionSourceProbe(paths).ProbeAsync(expected.InstructionSource, cancellationToken);
        if (source.Status != ContextualRoleInstructionSourceProbeStatus.Ready)
        {
            throw new InvalidOperationException($"The default contextual-role instruction source is not ready ({source.Status}); workspace initialization failed closed.");
        }

        var request = ContextualRoleRevisionMutationRequestHash.Apply(
            new ContextualRoleRevisionMutationRequest(
                OperationId,
                string.Empty,
                ContextualRoleRevisionMutationKind.Create,
                RoleId,
                ActorId,
                expected,
                null,
                _seedTimestamp));

        ContextualRoleRevisionMutationResult mutation;
        using (var store = new ContextualRoleRevisionStore(paths, workspaceId))
        {
            mutation = await store.MutateAsync(request, cancellationToken);
        }

        if (mutation.Status is not (ContextualRoleRevisionMutationStatus.Accepted or ContextualRoleRevisionMutationStatus.Recovered)
            || !Matches(expected, mutation.Revision)
            || mutation.Evidence is not { State: ContextualRoleLifecycleState.Active } evidence
            || evidence.CurrentIdentity != expected.Identity)
        {
            throw new InvalidOperationException($"The default contextual-role mutation was not proved exact and active ({mutation.Status}); workspace initialization failed closed.");
        }

        return await VerifyAsync(paths, cancellationToken);
    }

    internal static async Task<ContextualRoleRevisionPin> VerifyAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var expected = CreateRevision(workspaceId);
        if (!await IsReadyAsync(paths, cancellationToken))
        {
            throw new InvalidOperationException("The persisted default contextual role could not be re-read as the exact active revision; workspace initialization failed closed.");
        }

        return new ContextualRoleRevisionPin(expected.Identity, expected.ContentHash);
    }

    internal static bool IsReady(WorkspacePaths paths)
        => IsReadyAsync(paths, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<bool> IsReadyAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var expected = CreateRevision(workspaceId);
            var sourceProbe = new WorkspaceContextualRoleInstructionSourceProbe(paths);
            var source = await sourceProbe.ProbeAsync(expected.InstructionSource, cancellationToken);
            using var store = new ContextualRoleRevisionStore(paths, workspaceId);
            var revisionRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(expected.Identity), cancellationToken);
            var lifecycleRead = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(RoleId), cancellationToken);
            var confirmedSource = await sourceProbe.ProbeAsync(expected.InstructionSource, cancellationToken);
            return source.Status == ContextualRoleInstructionSourceProbeStatus.Ready
                && confirmedSource.Status == ContextualRoleInstructionSourceProbeStatus.Ready
                && revisionRead.Status == ContextualRoleRevisionReadStatus.Found
                && revisionRead.Disposition == ContextualRoleRevisionDisposition.Active
                && Matches(expected, revisionRead.Revision)
                && lifecycleRead.Status == ContextualRoleLifecycleReadStatus.Found
                && lifecycleRead.Snapshot is { State: ContextualRoleLifecycleState.Active } lifecycle
                && lifecycle.CurrentIdentity == expected.Identity;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static ContextualRoleRevision CreateRevision(string workspaceId)
    {
        var capabilityIds = LoopCapabilityRequirements
            .GetAssignedCapabilityIds(LoopCapabilityRequirements.CreateDefaultConversationManifest())
            .Select(capabilityId => capabilityId.Value)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var revision = new ContextualRoleRevision(
            ContextualRoleLimits.SchemaVersion,
            new ContextualRoleRevisionIdentity(RoleId, Revision),
            string.Empty,
            "Default assistant",
            "Provide the workspace's bounded default conversation assistance.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance(ActorId, _seedTimestamp, _seedTimestamp),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                InstructionReferenceId,
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(capabilityIds));
        return ContextualRoleRevisionContentHash.Apply(revision);
    }

    internal static bool Matches(ContextualRoleRevision expected, ContextualRoleRevision? actual)
        => actual is not null
            && expected.SchemaVersion == actual.SchemaVersion
            && expected.Identity == actual.Identity
            && string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal)
            && string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal)
            && string.Equals(expected.Purpose, actual.Purpose, StringComparison.Ordinal)
            && expected.Status == actual.Status
            && expected.Provenance == actual.Provenance
            && expected.WorkspaceApplicability.WorkspaceIds.SequenceEqual(actual.WorkspaceApplicability.WorkspaceIds, StringComparer.Ordinal)
            && expected.InstructionSource == actual.InstructionSource
            && expected.PolicyMaxima.CapabilityIds.SequenceEqual(actual.PolicyMaxima.CapabilityIds, StringComparer.Ordinal)
            && ContextualRoleRevisionContentHash.Matches(actual);
}
