using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Runtime;

internal sealed class HumanInputConversationOperationCacheEntry
{
    internal HumanInputConversationOperationCacheEntry(HumanInputResponseOperationInput input, long sequence)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Sequence = sequence;
        ActiveCallCount = 1;
    }

    internal HumanInputResponseOperationInput Input { get; }

    internal long Sequence { get; }

    internal bool IsEvictable => IsTerminal && ActiveCallCount == 0;

    private int ActiveCallCount { get; set; }

    private bool IsTerminal { get; set; }

    internal void Acquire()
    {
        ActiveCallCount++;
    }

    internal void Release(bool isTerminal)
    {
        if (ActiveCallCount > 0)
        {
            ActiveCallCount--;
        }

        IsTerminal |= isTerminal;
    }
}
