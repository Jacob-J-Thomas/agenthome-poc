namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Represents a bounded exception projection that never retains the source exception object.
/// </summary>
public sealed class RedactedExceptionSnapshot
{
    internal RedactedExceptionSnapshot(
        string typeName,
        string message,
        string? source,
        string? stackTrace,
        string? hResult,
        RedactedDataNode data,
        IReadOnlyList<RedactedExceptionSnapshot> innerExceptions,
        bool isMarker)
    {
        TypeName = typeName;
        Message = message;
        Source = source;
        StackTrace = stackTrace;
        HResult = hResult;
        Data = data;
        InnerExceptions = innerExceptions;
        IsMarker = isMarker;
    }

    /// <summary>Gets the sanitized exception type name or a deterministic marker.</summary>
    public string TypeName { get; }

    /// <summary>Gets the sanitized exception message or a deterministic marker.</summary>
    public string Message { get; }

    /// <summary>Gets the sanitized exception source when present.</summary>
    public string? Source { get; }

    /// <summary>Gets the sanitized stack trace when present.</summary>
    public string? StackTrace { get; }

    /// <summary>Gets the sanitized invariant exception result code, or <see langword="null"/> for a synthetic marker.</summary>
    public string? HResult { get; }

    /// <summary>Gets bounded sanitized exception data.</summary>
    public RedactedDataNode Data { get; }

    /// <summary>Gets bounded sanitized inner exceptions.</summary>
    public IReadOnlyList<RedactedExceptionSnapshot> InnerExceptions { get; }

    /// <summary>Gets whether this snapshot represents a limit, cycle, or read-failure marker.</summary>
    public bool IsMarker { get; }
}
