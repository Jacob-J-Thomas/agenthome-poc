using EmbodySense.Core.Startup.ContextualRoles.Models;

namespace EmbodySense.Core.Startup.ContextualRoles;

/// <summary>Reads every page of one bounded contextual-role catalog without truncating valid later authoring choices.</summary>
public static class ContextualRoleCatalogAggregator
{
    /// <summary>Maximum number of distinct current role choices accepted across the complete catalog.</summary>
    public const int MaximumRoleChoices = 4_096;

    private const int PageSize = 100;

    /// <summary>Reads the complete bounded catalog and fails closed on duplicate identities or non-progressing cursors.</summary>
    /// <param name="roles">The authoritative paginated role facade.</param>
    /// <param name="cancellationToken">Cancels any catalog page read.</param>
    /// <returns>A complete available catalog with no continuation cursor, or the source's fail-closed posture.</returns>
    public static async Task<ContextualRoleCatalogResponse> ReadAsync(
        IContextualRoleCatalogFacade roles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var choices = new List<ContextualRoleSnapshot>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        while (true)
        {
            var page = await roles.ReadCatalogAsync(cursor, PageSize, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(page.Status, "available", StringComparison.Ordinal))
            {
                return page;
            }

            foreach (var role in page.Roles)
            {
                var identity = $"{role.RoleId}\n{role.Revision}\n{role.ContentHash}";
                if (!identities.Add(identity) || choices.Count >= MaximumRoleChoices)
                {
                    return Ambiguous();
                }

                choices.Add(role);
            }

            if (page.NextCursor is null)
            {
                return new ContextualRoleCatalogResponse("available", choices, null, null);
            }
            if (string.Equals(page.NextCursor, cursor, StringComparison.Ordinal) || page.Roles.Count == 0)
            {
                return Ambiguous();
            }

            cursor = page.NextCursor;
        }
    }

    private static ContextualRoleCatalogResponse Ambiguous()
        => new("ambiguous", [], null, new ContextualRoleError("contextual_role_catalog_ambiguous", "The complete bounded contextual-role catalog could not be proved."));
}
