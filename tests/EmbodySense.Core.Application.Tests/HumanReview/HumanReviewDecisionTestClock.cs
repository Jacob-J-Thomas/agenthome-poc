using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionTestClock : IHumanReviewTrustedClock
{
    private readonly Queue<DateTimeOffset> _values;

    public HumanReviewDecisionTestClock(params DateTimeOffset[] values) => _values = new Queue<DateTimeOffset>(values);

    public int ReadCount { get; private set; }

    public DateTimeOffset UtcNow
    {
        get
        {
            ReadCount++;
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("No trusted test time remains.");
            }

            return _values.Count == 1 ? _values.Peek() : _values.Dequeue();
        }
    }
}
