namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one bounded redacted page of canonical Human Input request posture.</summary>
/// <param name="Status">The closed page-read disposition.</param>
/// <param name="StoreGeneration">The canonical ledger generation when safely established.</param>
/// <param name="Requests">The ordered redacted posture projections.</param>
/// <param name="NextCursor">The opaque next-page cursor when more posture exists.</param>
public sealed record HumanInputRequestPosturePage(
    HumanInputRequestPosturePageStatus Status,
    long StoreGeneration,
    IReadOnlyList<HumanInputRequestPosture> Requests,
    string? NextCursor)
{
    /// <summary>Gets a defensive immutable copy of the redacted posture projections.</summary>
    public IReadOnlyList<HumanInputRequestPosture> Requests { get; } = Requests is null ? null! : Array.AsReadOnly(Requests.ToArray());
}
