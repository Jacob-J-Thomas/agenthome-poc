using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class Sample3PersistenceDiagnosticCapture : IDisposable
{
    private const string LogPathEnvironmentVariable = "EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG";
    private const string ModeEnvironmentVariable = "EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_MODE";
    private const string ExitCodeMode = "exit-code";
    private const string TerminalSignalMode = "terminal-signal";
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly string? _logPath;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _exceptionCaptureActive;
    private bool _disposed;

    private Sample3PersistenceDiagnosticCapture(string? mode, string? logPath)
    {
        Mode = mode ?? string.Empty;
        _logPath = logPath;
        IsActive = Mode is ExitCodeMode or TerminalSignalMode && !string.IsNullOrWhiteSpace(_logPath);
        if (!IsActive)
        {
            return;
        }

        Record("capture-start", $"mode={Mode};os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    internal bool IsActive { get; }

    internal string Mode { get; }

    internal bool UseTerminalSignal => IsActive && string.Equals(Mode, TerminalSignalMode, StringComparison.Ordinal);

    internal static Sample3PersistenceDiagnosticCapture StartFromEnvironment()
        => new(Environment.GetEnvironmentVariable(ModeEnvironmentVariable), Environment.GetEnvironmentVariable(LogPathEnvironmentVariable));

    internal void Record(string stage, string detail)
    {
        if (!IsActive)
        {
            return;
        }

        _entries.Enqueue($"elapsed_ms={_stopwatch.Elapsed.TotalMilliseconds:F3};stage={Safe(stage)};detail={Safe(detail)}");
    }

    internal void BeginExceptionCapture()
    {
        if (!IsActive || _exceptionCaptureActive)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        _exceptionCaptureActive = true;
        Record("first-chance-capture-start", "scope=case-store-list");
    }

    internal void EndExceptionCapture()
    {
        if (!_exceptionCaptureActive)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        _exceptionCaptureActive = false;
        Record("first-chance-capture-stop", "scope=case-store-list");
    }

    internal void ProbeExclusiveReadLock(string lockPath)
    {
        if (!IsActive)
        {
            return;
        }

        Record("lock-probe-start", $"file={Path.GetFileName(lockPath)}");
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            Record("lock-probe-complete", $"opened=true;length={stream.Length}");
        }
        catch (Exception exception) when (IsClassifiedStoreException(exception))
        {
            RecordException("lock-probe-caught", exception);
        }
    }

    internal async Task ProbeJournalReadAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        if (!IsActive)
        {
            return;
        }

        Record("journal-probe-start", $"file={Path.GetFileName(journalPath)}");
        try
        {
            var bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            Record("journal-probe-complete", $"read=true;bytes={bytes.Length};text={Safe(Encoding.UTF8.GetString(bytes))}");
        }
        catch (Exception exception) when (IsClassifiedStoreException(exception))
        {
            RecordException("journal-probe-caught", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!IsActive)
        {
            return;
        }

        EndExceptionCapture();
        Record("capture-stop", $"mode={Mode}");
        try
        {
            var directory = Path.GetDirectoryName(_logPath!);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllLines(_logPath!, _entries, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SAMPLE3_DIAGNOSTIC_WRITE_FAILED type={exception.GetType().FullName} hresult=0x{exception.HResult:X8} message={Safe(exception.Message)}");
        }
    }

    private void OnFirstChanceException(object? _, FirstChanceExceptionEventArgs args)
    {
        if (IsClassifiedStoreException(args.Exception))
        {
            RecordException("first-chance", args.Exception);
        }
    }

    private void RecordException(string stage, Exception exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        Record(stage, $"type={exception.GetType().FullName};hresult=0x{exception.HResult:X8};native_error={nativeError};message={exception.Message};stack={exception.StackTrace}");
    }

    private static bool IsClassifiedStoreException(Exception exception)
        => exception is FormatException
            or InvalidDataException
            or OverflowException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException
            or NotSupportedException
            or PlatformNotSupportedException;

    private static string Safe(string? value)
        => (value ?? string.Empty).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
