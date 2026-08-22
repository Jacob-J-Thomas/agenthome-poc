using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Diagnostics;

/// <summary>Provides bounded, path-free diagnostic evidence for a custom-loop run persistence failure.</summary>
/// <param name="Stage">The persistence stage that failed.</param>
/// <param name="NativeErrorKind">The native error-code namespace, or none when no native code was available.</param>
/// <param name="NativeErrorCode">The non-negative platform error code, or <see langword="null"/> when unavailable.</param>
/// <remarks>The diagnostic never includes a path, artifact name, document content, exception message, or stack trace.</remarks>
public sealed record CustomLoopRunPersistenceDiagnostic(
    CustomLoopRunPersistenceDiagnosticStage Stage,
    CustomLoopRunPersistenceNativeErrorKind NativeErrorKind,
    long? NativeErrorCode)
{
    /// <summary>The exception-data key used to retain this bounded diagnostic through a port boundary.</summary>
    public const string ExceptionDataKey = "EmbodySense.CustomLoopRunPersistenceDiagnostic";

    /// <summary>Finds the nearest bounded persistence diagnostic attached to an exception or its inner exceptions.</summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns>The attached diagnostic, or <see langword="null"/> when no diagnostic was retained.</returns>
    public static CustomLoopRunPersistenceDiagnostic? Find(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[ExceptionDataKey] is CustomLoopRunPersistenceDiagnostic diagnostic)
            {
                return diagnostic;
            }
        }

        return null;
    }
}
