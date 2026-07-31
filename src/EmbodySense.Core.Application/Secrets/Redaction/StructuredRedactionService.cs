using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using EmbodySense.Core.Application.Secrets.Redaction.Models;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Application.Secrets.Redaction;

/// <summary>
/// Orchestrates bounded structured, header, URI, and exception projections over a per-use sensitive-value scope.
/// </summary>
/// <remarks>
/// This service never persists source objects or secret material. Unsupported values are replaced with a marker
/// instead of invoking arbitrary <see cref="object.ToString"/> implementations.
/// </remarks>
public sealed class StructuredRedactionService
{
    /// <summary>Marker emitted when a structure or exception exceeds its depth bound.</summary>
    public const string DepthLimitMarker = "[REDACTION_DEPTH_LIMIT]";

    /// <summary>Marker emitted when a projection exceeds its total node bound.</summary>
    public const string NodeLimitMarker = "[REDACTION_NODE_LIMIT]";

    /// <summary>Marker emitted when a collection exceeds its entry bound.</summary>
    public const string EntryLimitMarker = "[REDACTION_ENTRY_LIMIT]";

    /// <summary>Marker emitted for a cycle in an active traversal path.</summary>
    public const string CycleMarker = "[REDACTION_CYCLE]";

    /// <summary>Marker emitted when a hostile source cannot be read safely.</summary>
    public const string ReadFailureMarker = "[REDACTION_READ_FAILURE]";

    /// <summary>Marker emitted for an unsupported value that cannot be projected without executing arbitrary code.</summary>
    public const string UnsupportedValueMarker = "[REDACTION_UNSUPPORTED_VALUE]";

    /// <summary>
    /// Redacts a nested dictionary and its supported scalar or collection values within deterministic traversal bounds.
    /// </summary>
    /// <param name="value">The dictionary to project. Keys and all supported textual values are sanitized.</param>
    /// <param name="scope">The per-use sensitive-value scope.</param>
    /// <param name="limits">Optional traversal limits; bounded defaults are used when omitted.</param>
    /// <returns>An ordered structured projection and value-free summary.</returns>
    public RedactionProjection<RedactedDataNode> RedactStructure(IReadOnlyDictionary<string, object?> value, SensitiveRedactionScope scope, RedactionProjectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(scope);

        var context = new RedactionTraversalContext(scope, limits ?? new RedactionProjectionLimits());
        var result = RedactNode(value, depth: 0, context);
        return new RedactionProjection<RedactedDataNode>(result, context.Accumulator.ToSummary(scope));
    }

    /// <summary>
    /// Redacts ordered header names and values within deterministic traversal bounds.
    /// </summary>
    /// <param name="headers">Header entries to project.</param>
    /// <param name="scope">The per-use sensitive-value scope.</param>
    /// <param name="limits">Optional traversal limits; bounded defaults are used when omitted.</param>
    /// <returns>An ordered header projection and value-free summary.</returns>
    public RedactionProjection<IReadOnlyList<RedactedHeader>> RedactHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, SensitiveRedactionScope scope, RedactionProjectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(scope);

        var context = new RedactionTraversalContext(scope, limits ?? new RedactionProjectionLimits());
        var projected = ReadHeaders(headers, context);
        return new RedactionProjection<IReadOnlyList<RedactedHeader>>(projected, context.Accumulator.ToSummary(scope));
    }

    /// <summary>
    /// Redacts the original textual representation of a URI without normalizing away encoded derivatives.
    /// </summary>
    /// <param name="value">The URI to sanitize.</param>
    /// <param name="scope">The per-use sensitive-value scope.</param>
    /// <returns>The bounded sanitized original URI text and value-free text summary.</returns>
    public TextRedactionResult RedactUri(Uri value, SensitiveRedactionScope scope)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(scope);
        return scope.RedactText(value.OriginalString);
    }

    /// <summary>
    /// Redacts an exception graph without retaining or invoking <see cref="Exception.ToString"/> on the source.
    /// </summary>
    /// <param name="exception">The exception graph to project.</param>
    /// <param name="scope">The per-use sensitive-value scope.</param>
    /// <param name="limits">Optional traversal limits; bounded defaults are used when omitted.</param>
    /// <returns>A bounded exception projection and value-free summary.</returns>
    public RedactionProjection<RedactedExceptionSnapshot> RedactException(Exception exception, SensitiveRedactionScope scope, RedactionProjectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(scope);

        var context = new RedactionTraversalContext(scope, limits ?? new RedactionProjectionLimits());
        var result = RedactExceptionNode(exception, depth: 0, context);
        return new RedactionProjection<RedactedExceptionSnapshot>(result, context.Accumulator.ToSummary(scope));
    }

    private static RedactedDataNode RedactNode(object? value, int depth, RedactionTraversalContext context)
    {
        if (depth > context.Limits.MaxDepth)
        {
            context.Accumulator.MarkLimit();
            return Marker(DepthLimitMarker, context);
        }

        if (!context.Accumulator.TryVisit(context.Limits))
        {
            return Marker(NodeLimitMarker, context);
        }

        if (value is null)
        {
            return new RedactedDataNode(RedactedDataKind.Null, null, null, [], []);
        }

        if (value is string text)
        {
            return Text(Sanitize(text, context));
        }

        if (value is char character)
        {
            return Text(Sanitize(character.ToString(), context));
        }

        if (value is bool boolean)
        {
            return new RedactedDataNode(RedactedDataKind.Boolean, null, boolean, [], []);
        }

        if (TryFormatKnownScalar(value, out var scalar))
        {
            return Text(Sanitize(scalar, context));
        }

        if (!context.TryEnter(value))
        {
            context.Accumulator.MarkLimit();
            return Marker(CycleMarker, context);
        }

        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return RedactReadOnlyDictionary(readOnlyDictionary, depth, context);
            }

            if (value is IDictionary dictionary)
            {
                return RedactDictionary(dictionary, depth, context);
            }

            if (value is IEnumerable sequence)
            {
                return RedactSequence(sequence, depth, context);
            }

            context.Accumulator.MarkFailure();
            return Marker(UnsupportedValueMarker, context);
        }
        finally
        {
            context.Exit(value);
        }
    }

    private static RedactedDataNode RedactReadOnlyDictionary(IReadOnlyDictionary<string, object?> dictionary, int depth, RedactionTraversalContext context)
    {
        List<KeyValuePair<string, object?>> entries;
        try
        {
            entries = ReadBounded(dictionary, context.Limits.MaxCollectionEntries);
        }
        catch
        {
            context.Accumulator.MarkFailure();
            return Marker(ReadFailureMarker, context);
        }

        if (entries.Count > context.Limits.MaxCollectionEntries)
        {
            context.Accumulator.MarkLimit();
            return Marker(EntryLimitMarker, context);
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var properties = new List<RedactedDataProperty>(entries.Count);
        foreach (var entry in entries)
        {
            var key = Sanitize(entry.Key ?? "", context);
            properties.Add(new RedactedDataProperty(key, RedactNode(entry.Value, depth + 1, context)));
        }

        return Object(properties);
    }

    private static RedactedDataNode RedactDictionary(IDictionary dictionary, int depth, RedactionTraversalContext context)
    {
        List<KeyValuePair<string, object?>> entries;
        try
        {
            entries = ReadBounded(dictionary, context.Limits.MaxCollectionEntries, out var unsupportedKey);
            if (unsupportedKey)
            {
                context.Accumulator.MarkFailure();
                return Marker(UnsupportedValueMarker, context);
            }
        }
        catch
        {
            context.Accumulator.MarkFailure();
            return Marker(ReadFailureMarker, context);
        }

        if (entries.Count > context.Limits.MaxCollectionEntries)
        {
            context.Accumulator.MarkLimit();
            return Marker(EntryLimitMarker, context);
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var properties = entries.Select(entry => new RedactedDataProperty(Sanitize(entry.Key, context), RedactNode(entry.Value, depth + 1, context))).ToList();
        return Object(properties);
    }

    private static RedactedDataNode RedactSequence(IEnumerable sequence, int depth, RedactionTraversalContext context)
    {
        List<object?> entries;
        try
        {
            entries = ReadBounded(sequence, context.Limits.MaxCollectionEntries);
        }
        catch
        {
            context.Accumulator.MarkFailure();
            return Marker(ReadFailureMarker, context);
        }

        if (entries.Count > context.Limits.MaxCollectionEntries)
        {
            context.Accumulator.MarkLimit();
            return Marker(EntryLimitMarker, context);
        }

        var items = entries.Select(entry => RedactNode(entry, depth + 1, context)).ToList();
        return Array(items);
    }

    private static IReadOnlyList<RedactedHeader> ReadHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, RedactionTraversalContext context)
    {
        List<KeyValuePair<string, IEnumerable<string>>> entries;
        try
        {
            entries = ReadBounded(headers, context.Limits.MaxCollectionEntries);
        }
        catch
        {
            context.Accumulator.MarkFailure();
            var marker = Sanitize(ReadFailureMarker, context);
            return new ReadOnlyCollection<RedactedHeader>([new RedactedHeader(marker, [marker])]);
        }

        if (entries.Count > context.Limits.MaxCollectionEntries)
        {
            context.Accumulator.MarkLimit();
            var marker = Sanitize(EntryLimitMarker, context);
            return new ReadOnlyCollection<RedactedHeader>([new RedactedHeader(marker, [marker])]);
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var projected = new List<RedactedHeader>(entries.Count);
        foreach (var entry in entries)
        {
            if (!context.Accumulator.TryVisit(context.Limits))
            {
                var marker = Sanitize(NodeLimitMarker, context);
                projected.Add(new RedactedHeader(marker, [marker]));
                break;
            }

            List<string> values;
            try
            {
                values = ReadBounded(entry.Value, context.Limits.MaxCollectionEntries);
            }
            catch
            {
                context.Accumulator.MarkFailure();
                projected.Add(new RedactedHeader(Sanitize(entry.Key ?? "", context), [Sanitize(ReadFailureMarker, context)]));
                continue;
            }

            if (values.Count > context.Limits.MaxCollectionEntries)
            {
                context.Accumulator.MarkLimit();
                projected.Add(new RedactedHeader(Sanitize(entry.Key ?? "", context), [Sanitize(EntryLimitMarker, context)]));
                continue;
            }

            var projectedValues = new List<string>(values.Count);
            foreach (var value in values)
            {
                if (!context.Accumulator.TryVisit(context.Limits))
                {
                    projectedValues.Add(Sanitize(NodeLimitMarker, context));
                    break;
                }

                projectedValues.Add(Sanitize(value ?? "", context));
            }

            projected.Add(new RedactedHeader(Sanitize(entry.Key ?? "", context), new ReadOnlyCollection<string>(projectedValues)));
        }

        return new ReadOnlyCollection<RedactedHeader>(projected);
    }

    private static RedactedExceptionSnapshot RedactExceptionNode(Exception exception, int depth, RedactionTraversalContext context)
    {
        if (depth > context.Limits.MaxDepth)
        {
            context.Accumulator.MarkLimit();
            return ExceptionMarker(DepthLimitMarker, context);
        }

        if (!context.Accumulator.TryVisit(context.Limits))
        {
            return ExceptionMarker(NodeLimitMarker, context);
        }

        if (!context.TryEnter(exception))
        {
            context.Accumulator.MarkLimit();
            return ExceptionMarker(CycleMarker, context);
        }

        try
        {
            var typeName = Sanitize(exception.GetType().FullName ?? exception.GetType().Name, context);
            var message = ReadExceptionText(() => exception.Message, context) ?? "";
            var source = ReadExceptionText(() => exception.Source, context);
            var stackTrace = ReadExceptionText(() => exception.StackTrace, context);
            var data = ReadExceptionData(exception, depth, context);
            var innerExceptions = ReadInnerExceptions(exception, depth, context);
            return new RedactedExceptionSnapshot(typeName, message, source, stackTrace, exception.HResult, data, innerExceptions, isMarker: false);
        }
        finally
        {
            context.Exit(exception);
        }
    }

    private static RedactedDataNode ReadExceptionData(Exception exception, int depth, RedactionTraversalContext context)
    {
        try
        {
            return RedactNode(exception.Data, depth + 1, context);
        }
        catch
        {
            context.Accumulator.MarkFailure();
            return Marker(ReadFailureMarker, context);
        }
    }

    private static IReadOnlyList<RedactedExceptionSnapshot> ReadInnerExceptions(Exception exception, int depth, RedactionTraversalContext context)
    {
        IReadOnlyList<Exception> source = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is null ? [] : [exception.InnerException];

        if (source.Count > context.Limits.MaxCollectionEntries)
        {
            context.Accumulator.MarkLimit();
            return new ReadOnlyCollection<RedactedExceptionSnapshot>([ExceptionMarker(EntryLimitMarker, context)]);
        }

        return new ReadOnlyCollection<RedactedExceptionSnapshot>(source.Select(item => RedactExceptionNode(item, depth + 1, context)).ToList());
    }

    private static string? ReadExceptionText(Func<string?> reader, RedactionTraversalContext context)
    {
        try
        {
            var value = reader();
            return value is null ? null : Sanitize(value, context);
        }
        catch
        {
            context.Accumulator.MarkFailure();
            return Sanitize(ReadFailureMarker, context);
        }
    }

    private static string Sanitize(string value, RedactionTraversalContext context)
    {
        if (context.Accumulator.ProjectionLimitReached)
        {
            return "";
        }

        var result = context.Scope.RedactText(value);
        return context.Accumulator.TryAdd(result, context.Limits) ? result.Value : "";
    }

    private static List<T> ReadBounded<T>(IEnumerable<T> source, int limit)
    {
        var result = new List<T>(Math.Min(limit + 1, 256));
        using var enumerator = source.GetEnumerator();
        while (result.Count <= limit && enumerator.MoveNext())
        {
            result.Add(enumerator.Current);
        }

        return result;
    }

    private static List<object?> ReadBounded(IEnumerable source, int limit)
    {
        var result = new List<object?>(Math.Min(limit + 1, 256));
        var enumerator = source.GetEnumerator();
        try
        {
            while (result.Count <= limit && enumerator.MoveNext())
            {
                result.Add(enumerator.Current);
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return result;
    }

    private static List<KeyValuePair<string, object?>> ReadBounded(IDictionary source, int limit, out bool unsupportedKey)
    {
        var result = new List<KeyValuePair<string, object?>>(Math.Min(limit + 1, 256));
        unsupportedKey = false;
        var enumerator = source.GetEnumerator();
        try
        {
            while (result.Count <= limit && enumerator.MoveNext())
            {
                var entry = enumerator.Entry;
                if (entry.Key is not string key)
                {
                    unsupportedKey = true;
                    break;
                }

                result.Add(new KeyValuePair<string, object?>(key, entry.Value));
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return result;
    }

    private static bool TryFormatKnownScalar(object value, out string text)
    {
        text = value switch
        {
            byte item => item.ToString(CultureInfo.InvariantCulture),
            sbyte item => item.ToString(CultureInfo.InvariantCulture),
            short item => item.ToString(CultureInfo.InvariantCulture),
            ushort item => item.ToString(CultureInfo.InvariantCulture),
            int item => item.ToString(CultureInfo.InvariantCulture),
            uint item => item.ToString(CultureInfo.InvariantCulture),
            long item => item.ToString(CultureInfo.InvariantCulture),
            ulong item => item.ToString(CultureInfo.InvariantCulture),
            float item => item.ToString("R", CultureInfo.InvariantCulture),
            double item => item.ToString("R", CultureInfo.InvariantCulture),
            decimal item => item.ToString(CultureInfo.InvariantCulture),
            DateTime item => item.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset item => item.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan item => item.ToString("c", CultureInfo.InvariantCulture),
            Guid item => item.ToString("D"),
            Uri item => item.OriginalString,
            Enum item => item.ToString(),
            _ => ""
        };
        return text.Length != 0 || value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Uri or Enum;
    }

    private static RedactedDataNode Text(string value)
    {
        return new RedactedDataNode(RedactedDataKind.Text, value, null, [], []);
    }

    private static RedactedDataNode Marker(string value, RedactionTraversalContext context)
    {
        return new RedactedDataNode(RedactedDataKind.Marker, Sanitize(value, context), null, [], []);
    }

    private static RedactedDataNode Object(List<RedactedDataProperty> properties)
    {
        return new RedactedDataNode(RedactedDataKind.Object, null, null, new ReadOnlyCollection<RedactedDataProperty>(properties), []);
    }

    private static RedactedDataNode Array(List<RedactedDataNode> items)
    {
        return new RedactedDataNode(RedactedDataKind.Array, null, null, [], new ReadOnlyCollection<RedactedDataNode>(items));
    }

    private static RedactedExceptionSnapshot ExceptionMarker(string marker, RedactionTraversalContext context)
    {
        var safeMarker = Sanitize(marker, context);
        return new RedactedExceptionSnapshot(safeMarker, safeMarker, null, null, 0, Marker(marker, context), [], isMarker: true);
    }
}
