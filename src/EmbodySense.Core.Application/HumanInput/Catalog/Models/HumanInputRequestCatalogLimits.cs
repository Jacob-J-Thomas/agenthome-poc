namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Defines finite schema-1 bounds for canonical Human Input catalog reads.</summary>
public static class HumanInputRequestCatalogLimits
{
    /// <summary>Gets the maximum number of request aggregates returned by one page.</summary>
    public const int MaxPageSize = 64;
}
