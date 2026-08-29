namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Identifies one exact no-selection lifecycle disposition before it is atomically projected into the canonical run frontier.</summary>
internal enum NoResponseDisposition
{
    Unknown,
    Pending,
    Expired,
    Cancelled,
    Rejected,
    SupersessionUnresolved,
}
