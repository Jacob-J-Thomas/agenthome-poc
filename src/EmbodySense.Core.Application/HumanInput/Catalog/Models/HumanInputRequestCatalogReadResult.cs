namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Returns one exact canonical Human Input aggregate from the authenticated ledger.</summary>
/// <param name="Status">The closed read disposition.</param>
/// <param name="StoreGeneration">The authenticated ledger generation when safely established.</param>
/// <param name="Entry">The exact request aggregate when available.</param>
public sealed record HumanInputRequestCatalogReadResult(
    HumanInputRequestCatalogReadStatus Status,
    long StoreGeneration,
    HumanInputRequestCatalogEntry? Entry);
