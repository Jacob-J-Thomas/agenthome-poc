using EmbodySense.Core.Startup.ContextualRoles;
using EmbodySense.Core.Startup.ContextualRoles.Models;

namespace EmbodySense.Core.Startup.Tests.ContextualRoles;

internal sealed class PagedContextualRoleCatalogFacade : IContextualRoleCatalogFacade
{
    private readonly IReadOnlyList<ContextualRoleSnapshot> _roles;
    private readonly bool _repeatCursor;

    public PagedContextualRoleCatalogFacade(IReadOnlyList<ContextualRoleSnapshot> roles, bool repeatCursor = false)
    {
        _roles = roles;
        _repeatCursor = repeatCursor;
    }

    public List<string?> ObservedCursors { get; } = [];

    public Task<ContextualRoleCatalogResponse> ReadCatalogAsync(string? startAfterRoleId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservedCursors.Add(startAfterRoleId);
        var start = startAfterRoleId is null
            ? 0
            : _roles.ToList().FindIndex(role => string.Equals(role.RoleId, startAfterRoleId, StringComparison.Ordinal)) + 1;
        var page = _roles.Skip(start).Take(maximumCount).ToArray();
        var hasMore = start + page.Length < _roles.Count;
        var cursor = _repeatCursor ? startAfterRoleId ?? page.LastOrDefault()?.RoleId : hasMore ? page[^1].RoleId : null;
        return Task.FromResult(new ContextualRoleCatalogResponse("available", page, cursor, null));
    }

    public Task<ContextualRoleResponse> InspectAsync(ContextualRoleInspectionInput input, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
