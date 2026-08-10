using EmbodySense.Core.Startup.ContextualRoles.Models;

namespace EmbodySense.Core.Startup.ContextualRoles;

/// <summary>Defines the surface-neutral redacted contextual-role inspection boundary.</summary>
public interface IContextualRoleCatalogFacade
{
    /// <summary>Reads one bounded deterministic page of current role posture.</summary>
    Task<ContextualRoleCatalogResponse> ReadCatalogAsync(string? startAfterRoleId, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Validates one exact caller-observed role revision and registered source.</summary>
    Task<ContextualRoleResponse> InspectAsync(ContextualRoleInspectionInput input, CancellationToken cancellationToken = default);
}
