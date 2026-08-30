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
    private readonly Dictionary<string, HumanInputResponseOperationInput> _operations = new(StringComparer.Ordinal);

    internal bool TryGet(
        string operationId,
        HumanInputResponseOperationKind kind,
        string requestId,
        string? responseId,
        HumanInputResponseValue? value,
        string? explanation,
        out HumanInputResponseOperationInput? input)
    {
        lock (_operations)
        {
            if (!_operations.TryGetValue(operationId, out var retained))
            {
                input = null;
                return true;
            }

            if (!MatchesIntent(retained, kind, requestId, responseId, value, explanation))
            {
                input = null;
                return false;
            }

            input = Capture(retained);
            return true;
        }
    }

    internal bool TryAdd(HumanInputResponseOperationInput input, out HumanInputResponseOperationInput? retained)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_operations)
        {
            if (_operations.TryGetValue(input.OperationId, out var existing))
            {
                retained = Capture(existing);
                return MatchesIntent(existing, input.Kind, input.RequestId, input.ResponseId, input.Value, input.Explanation);
            }

            if (_operations.Count >= MaximumOperations)
            {
                retained = null;
                return false;
            }

            retained = Capture(input);
            _operations.Add(input.OperationId, retained);
            retained = Capture(retained);
            return true;
        }
    }

    private static bool MatchesIntent(
        HumanInputResponseOperationInput input,
        HumanInputResponseOperationKind kind,
        string requestId,
        string? responseId,
        HumanInputResponseValue? value,
        string? explanation)
        => input.Kind == kind
            && string.Equals(input.RequestId, requestId, StringComparison.Ordinal)
            && string.Equals(input.ResponseId, responseId, StringComparison.Ordinal)
            && ValueEquals(input.Value, value)
            && string.Equals(input.Explanation, explanation, StringComparison.Ordinal);

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
