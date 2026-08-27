using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryTestClock : IHumanReviewTrustedClock
{
    private readonly IReadOnlyList<DateTimeOffset> _values;
    private readonly Exception? _exception;
    private readonly int? _failureReadNumber;
    private int _readCount;

    public HumanReviewContinuationRecoveryTestClock(DateTimeOffset now, Exception? exception = null)
        : this([now], exception is null ? null : 1, exception)
    {
    }

    public HumanReviewContinuationRecoveryTestClock(IReadOnlyList<DateTimeOffset> values, int? failureReadNumber = null, Exception? exception = null)
    {
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _failureReadNumber = failureReadNumber;
        _exception = exception;
    }

    public int ReadCount => _readCount;

    public DateTimeOffset UtcNow
    {
        get
        {
            _readCount++;
            if (_failureReadNumber == _readCount)
            {
                throw _exception ?? new InvalidOperationException();
            }

            return _values.Count == 0
                ? default
                : _values[Math.Min(_readCount - 1, _values.Count - 1)];
        }
    }
}
