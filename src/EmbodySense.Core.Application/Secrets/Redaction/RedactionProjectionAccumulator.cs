using EmbodySense.Core.Application.Secrets.Redaction.Models;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Application.Secrets.Redaction;

internal sealed class RedactionProjectionAccumulator
{
    private int _failureMarkerCount;
    private int _limitMarkerCount;
    private int _projectedCharacterCount;
    private int _textReplacementCount;
    private int _visitedNodeCount;
    private bool _projectionLimitReached;

    public bool ProjectionLimitReached => _projectionLimitReached;

    public bool TryVisit(RedactionProjectionLimits limits)
    {
        if (_visitedNodeCount >= limits.MaxNodes)
        {
            MarkLimit();
            return false;
        }

        _visitedNodeCount++;
        return true;
    }

    public bool TryAdd(TextRedactionResult result, RedactionProjectionLimits limits)
    {
        _textReplacementCount += result.Summary.ReplacementCount;
        if (result.Summary.Status != RedactionStatus.Completed)
        {
            MarkLimit();
        }

        if (_projectionLimitReached)
        {
            return false;
        }

        if (result.Value.Length > limits.MaxProjectedCharacters - _projectedCharacterCount)
        {
            _projectionLimitReached = true;
            MarkLimit();
            return false;
        }

        _projectedCharacterCount += result.Value.Length;
        return true;
    }

    public void MarkLimit()
    {
        _limitMarkerCount++;
    }

    public void MarkFailure()
    {
        _failureMarkerCount++;
    }

    public RedactionProjectionSummary ToSummary(SensitiveRedactionScope scope)
    {
        return new RedactionProjectionSummary(scope.SensitiveValueCount, scope.IgnoredValueCount, _textReplacementCount, _visitedNodeCount, _projectedCharacterCount, _limitMarkerCount, _failureMarkerCount);
    }
}
