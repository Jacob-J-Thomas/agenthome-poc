using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Application.HumanInput.Continuations;

/// <summary>Retains one closed response-selection attachment result without carrying response values.</summary>
internal sealed record SelectionAttachment(SelectionAttachmentStatus Status, HumanInputResponseSelectionReference? Selection)
{
    internal static SelectionAttachment Attached(HumanInputResponseSelectionReference selection) => new(SelectionAttachmentStatus.Attached, selection);

    internal static SelectionAttachment Replayed(HumanInputResponseSelectionReference selection) => new(SelectionAttachmentStatus.Replayed, selection);

    internal static SelectionAttachment Stale() => new(SelectionAttachmentStatus.Stale, null);

    internal static SelectionAttachment Invalid() => new(SelectionAttachmentStatus.Invalid, null);

    internal static SelectionAttachment Unavailable() => new(SelectionAttachmentStatus.Unavailable, null);

    internal static SelectionAttachment Retired() => new(SelectionAttachmentStatus.Retired, null);

    internal static SelectionAttachment NoWork() => new(SelectionAttachmentStatus.NoWork, null);
}
