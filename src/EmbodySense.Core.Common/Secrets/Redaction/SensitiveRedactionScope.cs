using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Secrets;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Common.Secrets.Redaction;

/// <summary>
/// Owns a per-use, bounded set of sensitive values and supported encoded derivatives for deterministic text redaction.
/// </summary>
/// <remarks>
/// The scope is defense in depth: it only claims to replace explicitly supplied values and the derivatives documented
/// by <see cref="Create"/>. It is not authorization and cannot prove that unknown sensitive material was removed.
/// Instances are safe for concurrent redaction calls, but disposal must not race with active callers.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SensitiveRedactionScope : IDisposable
{
    /// <summary>Marker used for each supported sensitive pattern found in a completed projection.</summary>
    public const string ReplacementMarker = "[REDACTED]";

    /// <summary>Marker returned when the sensitive-value scope exceeds a configured bound.</summary>
    public const string ScopeLimitMarker = "[REDACTION_SCOPE_LIMIT]";

    /// <summary>Marker returned when input exceeds a configured bound.</summary>
    public const string InputLimitMarker = "[REDACTION_INPUT_LIMIT]";

    /// <summary>Marker returned when projected output would exceed a configured bound.</summary>
    public const string OutputLimitMarker = "[REDACTION_OUTPUT_LIMIT]";

    /// <summary>Marker returned when deterministic comparison work exceeds a configured bound.</summary>
    public const string WorkLimitMarker = "[REDACTION_WORK_LIMIT]";

    /// <summary>Marker returned when replacement text would synthesize another scoped sensitive value.</summary>
    public const string ProjectionSafetyMarker = "[REDACTION_PROJECTION_UNSAFE]";

    private const string ProjectionMarker = "[sensitive-redaction-scope]";
    private static readonly string[] _safeMarkerFallbacks = ["[MASKED]", "***"];
    private readonly object _sync = new();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private List<SensitiveRedactionPattern>? _patterns;

    private SensitiveRedactionScope(RedactionLimits limits, List<SensitiveRedactionPattern> patterns, int sensitiveValueCount, int ignoredValueCount, bool isValid)
    {
        Limits = limits;
        _patterns = patterns;
        SensitiveValueCount = sensitiveValueCount;
        IgnoredValueCount = ignoredValueCount;
        IsValid = isValid;
    }

    /// <summary>Gets the hard-bounded limits applied by this scope.</summary>
    public RedactionLimits Limits { get; }

    /// <summary>Gets the number of non-empty, distinct sensitive values admitted to this scope.</summary>
    public int SensitiveValueCount { get; }

    /// <summary>Gets the number of empty or duplicate supplied values ignored by this scope.</summary>
    public int IgnoredValueCount { get; }

    /// <summary>Gets whether every supplied value fit within the configured scope limits.</summary>
    public bool IsValid { get; }

    /// <summary>Gets whether this scope has cleared and released all owned patterns.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _patterns is null;
            }
        }
    }

    private string DebuggerDisplay => GetProjectionMarker();

    /// <summary>
    /// Creates a per-use scope by copying each explicitly supplied value and its RFC 3986 and .NET URI
    /// percent-encoded, form-encoded, and standard Base64 derivatives into disposable owned memory.
    /// </summary>
    /// <param name="sensitiveValues">Bounded temporary value owners. Their public API never exposes plaintext.</param>
    /// <param name="limits">Optional hard-bounded limits; defaults are used when omitted.</param>
    /// <returns>
    /// A valid scope, or a fail-closed invalid scope when count, size, null, or disposed-value constraints are violated.
    /// Empty and duplicate values are safely ignored.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sensitiveValues"/> is <see langword="null"/>.</exception>
    public static SensitiveRedactionScope Create(IReadOnlyList<EphemeralSecretMaterial> sensitiveValues, RedactionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sensitiveValues);
        limits ??= new RedactionLimits();

        if (sensitiveValues.Count > limits.MaxSensitiveValues)
        {
            return new SensitiveRedactionScope(limits, [], 0, 0, isValid: false);
        }

        var patterns = new List<SensitiveRedactionPattern>();
        var acceptedValues = new List<char[]>();
        var suppliedValueCount = 0;
        var ignoredValueCount = 0;
        var isValid = true;

        try
        {
            foreach (var material in sensitiveValues)
            {
                if (suppliedValueCount >= limits.MaxSensitiveValues)
                {
                    isValid = false;
                    break;
                }

                suppliedValueCount++;
                if (material is null)
                {
                    isValid = false;
                    break;
                }

                char[] value;
                try
                {
                    value = material.CopyCharacters();
                }
                catch (ObjectDisposedException)
                {
                    isValid = false;
                    break;
                }

                if (value.Length == 0)
                {
                    ignoredValueCount++;
                    continue;
                }

                if (value.Length > limits.MaxSensitiveValueCharacters)
                {
                    Zero(value);
                    isValid = false;
                    break;
                }

                if (acceptedValues.Any(existing => existing.AsSpan().SequenceEqual(value)))
                {
                    Zero(value);
                    ignoredValueCount++;
                    continue;
                }

                acceptedValues.Add(value);
                SensitiveRedactionPatternFactory.AddSupportedPatterns(patterns, value);
            }

            if (!isValid)
            {
                DisposePatterns(patterns);
            }
            else
            {
                patterns.Sort(static (left, right) =>
                {
                    var lengthComparison = right.Length.CompareTo(left.Length);
                    return lengthComparison != 0 ? lengthComparison : left.Characters.SequenceCompareTo(right.Characters);
                });
            }

            return new SensitiveRedactionScope(limits, patterns, isValid ? acceptedValues.Count : 0, ignoredValueCount, isValid);
        }
        catch
        {
            DisposePatterns(patterns);
            throw;
        }
        finally
        {
            foreach (var acceptedValue in acceptedValues)
            {
                Zero(acceptedValue);
            }
        }
    }

    /// <summary>
    /// Replaces explicitly scoped values and supported derivatives in one bounded text input.
    /// </summary>
    /// <param name="value">Text to inspect. Unknown sensitive values cannot be detected by this operation.</param>
    /// <returns>A bounded projection and value-free operation summary.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when this scope has already been disposed.</exception>
    public TextRedactionResult RedactText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_patterns is null, this);
            if (!IsValid)
            {
                return CreateLimitResult(ScopeLimitMarker, RedactionStatus.ScopeLimitExceeded, examinedCharacterCount: 0, workUnitCount: 0);
            }

            if (value.Length > Limits.MaxInputCharacters)
            {
                return CreateLimitResult(InputLimitMarker, RedactionStatus.InputLimitExceeded, examinedCharacterCount: 0, workUnitCount: 0);
            }

            return RedactWithinLimits(value, _patterns);
        }
    }

    /// <summary>
    /// Returns a value-free diagnostic projection.
    /// </summary>
    /// <returns>A value-free marker, or an empty string when no constant marker is safe for the scoped patterns.</returns>
    public override string ToString()
    {
        return GetProjectionMarker();
    }

    /// <summary>
    /// Clears every owned sensitive pattern and releases this scope. Repeated calls are safe.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_patterns is null)
            {
                return;
            }

            foreach (var pattern in _patterns)
            {
                pattern.Dispose();
            }

            _patterns.Clear();
            _patterns = null;
        }
    }

    private TextRedactionResult RedactWithinLimits(string value, IReadOnlyList<SensitiveRedactionPattern> patterns)
    {
        var builder = new StringBuilder(Math.Min(value.Length, Limits.MaxOutputCharacters));
        var replacementMarker = GetSafeMarker(ReplacementMarker, patterns);
        var replacementCount = 0;
        var examinedCharacterCount = 0;
        var workUnitCount = 0;
        var input = value.AsSpan();

        for (var index = 0; index < input.Length;)
        {
            examinedCharacterCount++;
            SensitiveRedactionPattern? match = null;
            var matchedLength = 0;
            foreach (var pattern in patterns)
            {
                workUnitCount++;
                if (workUnitCount > Limits.MaxWorkUnits)
                {
                    return CreateLimitResult(WorkLimitMarker, RedactionStatus.WorkLimitExceeded, examinedCharacterCount, Limits.MaxWorkUnits, replacementCount);
                }

                if (pattern.Length > input.Length - index)
                {
                    continue;
                }

                if (pattern.TryMatch(input, index, ref workUnitCount, Limits.MaxWorkUnits, out var candidateMatchedLength, out var workLimitExceeded)
                    && candidateMatchedLength > matchedLength)
                {
                    match = pattern;
                    matchedLength = candidateMatchedLength;
                }

                if (workLimitExceeded)
                {
                    return CreateLimitResult(WorkLimitMarker, RedactionStatus.WorkLimitExceeded, examinedCharacterCount, Limits.MaxWorkUnits, replacementCount);
                }
            }

            if (match is null)
            {
                builder.Append(input[index]);
                index++;
            }
            else
            {
                builder.Append(replacementMarker);
                index += matchedLength;
                replacementCount++;
            }

            if (builder.Length > Limits.MaxOutputCharacters)
            {
                return CreateLimitResult(OutputLimitMarker, RedactionStatus.OutputLimitExceeded, examinedCharacterCount, workUnitCount, replacementCount);
            }
        }

        var projection = builder.ToString();
        if (ContainsSupportedPattern(projection, patterns, ref workUnitCount, out var projectionWorkLimitExceeded))
        {
            return CreateLimitResult(ProjectionSafetyMarker, RedactionStatus.ProjectionSafetyFailed, examinedCharacterCount, workUnitCount, replacementCount);
        }

        if (projectionWorkLimitExceeded)
        {
            return CreateLimitResult(WorkLimitMarker, RedactionStatus.WorkLimitExceeded, examinedCharacterCount, Limits.MaxWorkUnits, replacementCount);
        }

        return new TextRedactionResult(projection, CreateSummary(RedactionStatus.Completed, replacementCount, examinedCharacterCount, workUnitCount));
    }

    private TextRedactionResult CreateLimitResult(string marker, RedactionStatus status, int examinedCharacterCount, int workUnitCount, int replacementCount = 0)
    {
        var safeMarker = status == RedactionStatus.ScopeLimitExceeded ? "" : GetSafeMarker(marker, _patterns ?? []);
        if (safeMarker.Length > Limits.MaxOutputCharacters)
        {
            safeMarker = "";
        }

        return new TextRedactionResult(safeMarker, CreateSummary(status, replacementCount, examinedCharacterCount, workUnitCount));
    }

    private RedactionSummary CreateSummary(RedactionStatus status, int replacementCount, int examinedCharacterCount, int workUnitCount)
    {
        return new RedactionSummary(status, SensitiveValueCount, IgnoredValueCount, replacementCount, examinedCharacterCount, workUnitCount);
    }

    private static string GetSafeMarker(string preferred, IReadOnlyList<SensitiveRedactionPattern> patterns)
    {
        if (!ContainsPattern(preferred, patterns))
        {
            return preferred;
        }

        foreach (var fallback in _safeMarkerFallbacks)
        {
            if (!ContainsPattern(fallback, patterns))
            {
                return fallback;
            }
        }

        return "";
    }

    private string GetProjectionMarker()
    {
        lock (_sync)
        {
            return _patterns is null || !IsValid ? "" : GetSafeMarker(ProjectionMarker, _patterns);
        }
    }

    private static bool ContainsPattern(string candidate, IReadOnlyList<SensitiveRedactionPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (candidate.AsSpan().Contains(pattern.Characters, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsSupportedPattern(string candidate, IReadOnlyList<SensitiveRedactionPattern> patterns, ref int workUnitCount, out bool workLimitExceeded)
    {
        workLimitExceeded = false;
        var input = candidate.AsSpan();
        for (var index = 0; index < input.Length; index++)
        {
            foreach (var pattern in patterns)
            {
                workUnitCount++;
                if (workUnitCount > Limits.MaxWorkUnits)
                {
                    workLimitExceeded = true;
                    return false;
                }

                if (pattern.Length > input.Length - index)
                {
                    continue;
                }

                if (pattern.TryMatch(input, index, ref workUnitCount, Limits.MaxWorkUnits, out _, out workLimitExceeded))
                {
                    return true;
                }

                if (workLimitExceeded)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static void DisposePatterns(List<SensitiveRedactionPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            pattern.Dispose();
        }

        patterns.Clear();
    }

    private static void Zero(char[] value)
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
