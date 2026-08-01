using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal static class LoopRunConversationPublicationDispositionProjector
{
    internal static IReadOnlyList<LoopRunConversationPublicationDispositionSnapshot> Project(CustomLoopRunRecord run)
    {
        return run.Events
            .Where(item => !string.IsNullOrWhiteSpace(item.ConversationPublicationId))
            .GroupBy(item => item.ConversationPublicationId!, StringComparer.Ordinal)
            .OrderBy(group => group.Min(item => item.Sequence))
            .Select(group => Project(run, group.Key, group.OrderBy(item => item.Sequence).ToArray()))
            .ToArray();
    }

    private static LoopRunConversationPublicationDispositionSnapshot Project(CustomLoopRunRecord run, string operationId, CustomLoopRunEvent[] events)
    {
        var terminalEvents = events.Where(item => item.Kind == CustomLoopRunEventKind.ConversationPublished).ToArray();
        if (terminalEvents.Length == 0)
        {
            return Snapshot(operationId, "Pending", "Publication intent or output selection is recorded, but no terminal publication outcome has been committed.", false, false, events);
        }

        if (terminalEvents.Length > 1)
        {
            var dispositions = terminalEvents.Select(item => TerminalDisposition(run, item)).Distinct(StringComparer.Ordinal).ToArray();
            var conflicting = dispositions.Length > 1;
            return Snapshot(
                operationId,
                conflicting ? "ConflictingTerminalOutcomes" : "DuplicateTerminalOutcomes",
                conflicting
                    ? "Multiple terminal publication outcomes disagree; inspect the ordered evidence before retrying."
                    : "Multiple terminal publication outcomes were recorded; inspect the ordered evidence before retrying.",
                false,
                true,
                events);
        }

        var disposition = TerminalDisposition(run, terminalEvents[0]);
        return disposition switch
        {
            "Published" => Snapshot(operationId, disposition, "The canonical output was durably published to the invoking conversation.", true, false, events),
            "AlreadyPublished" => Snapshot(operationId, disposition, "The idempotent publication was already committed and was reconciled without a duplicate append.", true, false, events),
            "OmittedNoInvokingConversation" => Snapshot(operationId, disposition, "Publication was selected but omitted because admission bound no invoking conversation.", true, false, events),
            "DefinitelyFailed" => Snapshot(operationId, disposition, "Publication definitely failed; no successful conversation append is reported.", true, false, events),
            _ => Snapshot(operationId, "Uncertain", "The publication outcome is not definite and requires review before retrying.", false, false, events)
        };
    }

    private static string TerminalDisposition(CustomLoopRunRecord run, CustomLoopRunEvent terminalEvent)
    {
        if (terminalEvent.PublishedToInvokingConversation == true)
        {
            return terminalEvent.Detail.Contains("already committed", StringComparison.Ordinal) ? "AlreadyPublished" : "Published";
        }

        if (run.InvokingConversation is null)
        {
            return "OmittedNoInvokingConversation";
        }

        return run.Status == CustomLoopRunStatus.Failed ? "DefinitelyFailed" : "Uncertain";
    }

    private static LoopRunConversationPublicationDispositionSnapshot Snapshot(string operationId, string disposition, string detail, bool isDefinite, bool hasIntegrityWarning, IReadOnlyList<CustomLoopRunEvent> events)
    {
        return new LoopRunConversationPublicationDispositionSnapshot(operationId, disposition, detail, isDefinite, hasIntegrityWarning, events.Select(item => item.Sequence).ToArray());
    }
}
