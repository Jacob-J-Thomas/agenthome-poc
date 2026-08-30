using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>Retains bounded private response-operation intent for an interactive runtime session.</summary>
/// <remarks>The cache exists only to retry the exact original facade command after a transport-ambiguous outcome. It is never
/// persisted, projected to model context, added to transcript history, or exposed through diagnostics. Reusing an operation id
/// with different untrusted data fails closed.</remarks>
internal sealed class HumanInputConversationOperationCache
{
    private const int MaximumOperations = 256;
    private readonly Dictionary<string, HumanInputConversationOperationCacheEntry> _operations = new(StringComparer.Ordinal);
    private long _nextSequence;

    internal bool TryAcquire(HumanInputResponseOperationInput input, out HumanInputResponseOperationInput? retained)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_operations)
        {
            if (_operations.TryGetValue(input.OperationId, out var existing))
            {
                if (!MatchesIntent(existing.Input, input))
                {
                    retained = null;
                    return false;
                }

                existing.Acquire();
                retained = Capture(existing.Input);
                return true;
            }

            if (_operations.Count >= MaximumOperations && !TryEvictOldestTerminal())
            {
                retained = null;
                return false;
            }

            retained = Capture(input);
            _operations.Add(input.OperationId, new HumanInputConversationOperationCacheEntry(retained, _nextSequence++));
            retained = Capture(retained);
            return true;
        }
    }

    internal void Release(string operationId, HumanInputOperationStatus? status)
    {
        lock (_operations)
        {
            if (!_operations.TryGetValue(operationId, out var entry))
            {
                return;
            }

            entry.Release(IsTerminal(status));
        }
    }

    private bool TryEvictOldestTerminal()
    {
        var candidate = _operations
            .Where(pair => pair.Value.IsEvictable)
            .OrderBy(pair => pair.Value.Sequence)
            .FirstOrDefault();
        return candidate.Value is not null && _operations.Remove(candidate.Key);
    }

    private static bool IsTerminal(HumanInputOperationStatus? status)
        => status is not null and not HumanInputOperationStatus.Unknown and not HumanInputOperationStatus.Unavailable and not HumanInputOperationStatus.Ambiguous;

    private static bool MatchesIntent(HumanInputResponseOperationInput left, HumanInputResponseOperationInput right)
        => left.Kind == right.Kind
            && string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
            && left.ExpectedLifecycleVersion == right.ExpectedLifecycleVersion
            && left.ExpectedLifecycleStatus == right.ExpectedLifecycleStatus
            && Equals(left.ExpectedRequest, right.ExpectedRequest)
            && string.Equals(left.ResponseId, right.ResponseId, StringComparison.Ordinal)
            && ValueEquals(left.Value, right.Value)
            && string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal);

    private static bool ValueEquals(HumanInputResponseValue? left, HumanInputResponseValue? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Kind != right.Kind
            || !string.Equals(left.Text, right.Text, StringComparison.Ordinal)
            || !string.Equals(left.ChoiceId, right.ChoiceId, StringComparison.Ordinal)
            || left.Confirmation != right.Confirmation
            || !Equals(left.Reference, right.Reference))
        {
            return false;
        }

        var leftFields = left.StructuredFields;
        var rightFields = right.StructuredFields;
        if (leftFields is null || rightFields is null)
        {
            return leftFields is null && rightFields is null;
        }

        return leftFields.Value.SequenceEqual(rightFields.Value);
    }

    private static HumanInputResponseOperationInput Capture(HumanInputResponseOperationInput source)
    {
        ImmutableArray<HumanInputStructuredFieldValue>? fields = source.Value?.StructuredFields is { } sourceFields
            ? sourceFields.Select(field => new HumanInputStructuredFieldValue(field.FieldId, field.Text, field.ChoiceId)).ToImmutableArray()
            : null;
        var value = source.Value is null
            ? null
            : new HumanInputResponseValue(
                source.Value.Kind,
                source.Value.Text,
                source.Value.ChoiceId,
                source.Value.Confirmation,
                fields,
                source.Value.Reference is null ? null : new HumanInputReference(source.Value.Reference.Kind, source.Value.Reference.Value));
        return new HumanInputResponseOperationInput(
            source.OperationId,
            source.Kind,
            source.RequestId,
            source.ExpectedLifecycleVersion,
            source.ExpectedLifecycleStatus,
            source.ExpectedRequest with { },
            source.ResponseId,
            value,
            source.Explanation);
    }
}
