namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Classifies the canonical response-selection or no-response terminal attachment attempt before generic wake submission.</summary>
internal enum SelectionAttachmentStatus
{
    Attached,
    Replayed,
    Stale,
    Invalid,
    Unavailable,
    Retired,
    NoWork,
}
