namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one exact redacted Human Input posture projection.</summary>
/// <param name="Status">The closed read disposition.</param>
/// <param name="StoreGeneration">The canonical ledger generation when safely established.</param>
/// <param name="Request">The exact redacted posture when available.</param>
public sealed record HumanInputRequestPostureReadResult(
    HumanInputRequestPostureReadStatus Status,
    long StoreGeneration,
    HumanInputRequestPosture? Request);
