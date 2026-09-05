using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class CrossProcessProcessOwnership : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StandardInputHandle = unchecked((uint)-10);
    private const uint StandardOutputHandle = unchecked((uint)-11);
    private const uint StandardErrorHandle = unchecked((uint)-12);
    private const uint Infinite = 0xFFFFFFFF;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;

    private readonly Process _process;
    private readonly SafeFileHandle? _nativeProcessHandle;
    private readonly SafeFileHandle? _job;
    private readonly StreamReader? _standardOutput;
    private readonly StreamReader? _standardError;
    private readonly StreamWriter? _standardInput;
    private readonly object _streamReadGate = new();

    private int _activeStreamReads;
    private bool _streamDisposalRequested;
    private bool _streamsDisposed;

    private CrossProcessProcessOwnership(
        Process process,
        SafeFileHandle? nativeProcessHandle,
        SafeFileHandle? job,
        StreamReader? standardOutput,
        StreamReader? standardError,
        StreamWriter? standardInput)
    {
        _process = process;
        _nativeProcessHandle = nativeProcessHandle;
        _job = job;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _standardInput = standardInput;
    }

    internal StreamReader StandardOutput
        => _standardOutput ?? throw new InvalidOperationException("Cross-process standard output was not redirected.");

    internal StreamReader StandardError
        => _standardError ?? throw new InvalidOperationException("Cross-process standard error was not redirected.");

    internal StreamWriter StandardInput
        => _standardInput ?? throw new InvalidOperationException("Cross-process standard input was not redirected.");

    internal bool HasExited => _nativeProcessHandle is null
        ? _process.HasExited
        : GetExitCode() != StillActive;

    internal int ExitCode
    {
        get
        {
            var exitCode = GetExitCode();
            if (exitCode == StillActive)
            {
                throw new InvalidOperationException("The cross-process child has not exited.");
            }

            return unchecked((int)exitCode);
        }
    }

    internal int Id
    {
        get
        {
            if (_nativeProcessHandle is null)
            {
                return _process.Id;
            }

            var processId = GetProcessId(_nativeProcessHandle.DangerousGetHandle());
            if (processId == 0)
            {
                throw LastWin32Exception("The cross-process child ID could not be read.");
            }

            return checked((int)processId);
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        var nativeProcessHandle = _nativeProcessHandle;
        if (nativeProcessHandle is null)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return;
        }

        var nativeHandleReferenceAdded = false;
        try
        {
            nativeProcessHandle.DangerousAddRef(ref nativeHandleReferenceAdded);
            var nativeHandle = nativeProcessHandle.DangerousGetHandle();
            while (GetExitCode(nativeHandle) == StillActive)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
        finally
        {
            if (nativeHandleReferenceAdded)
            {
                nativeProcessHandle.DangerousRelease();
            }
        }
    }

    internal async Task WaitForTerminalSignalAsync(CancellationToken cancellationToken = default)
    {
        var nativeProcessHandle = _nativeProcessHandle;
        if (nativeProcessHandle is null)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return;
        }

        var nativeHandleReferenceAdded = false;
        try
        {
            nativeProcessHandle.DangerousAddRef(ref nativeHandleReferenceAdded);
            var nativeHandle = nativeProcessHandle.DangerousGetHandle();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wait = WaitForSingleObject(nativeHandle, 0);
                if (wait == WaitObject0)
                {
                    return;
                }
                if (wait == WaitFailed)
                {
                    throw LastWin32Exception("The cross-process child terminal signal could not be observed.");
                }
                if (wait != WaitTimeout)
                {
                    throw new InvalidOperationException($"The cross-process child returned unsupported wait status {wait}.");
                }
                await Task.Delay(10, cancellationToken);
            }
        }
        finally
        {
            if (nativeHandleReferenceAdded)
            {
                nativeProcessHandle.DangerousRelease();
            }
        }
    }

    internal string GetTerminalSignalSnapshot()
    {
        var nativeProcessHandle = _nativeProcessHandle;
        if (nativeProcessHandle is null)
        {
            return $"source=managed-process;signaled={_process.HasExited.ToString().ToLowerInvariant()};wait_status=managed;hresult=0x00000000;native_error=0";
        }

        var nativeHandleReferenceAdded = false;
        try
        {
            nativeProcessHandle.DangerousAddRef(ref nativeHandleReferenceAdded);
            var wait = WaitForSingleObject(nativeProcessHandle.DangerousGetHandle(), 0);
            var nativeError = wait == WaitFailed ? Marshal.GetLastWin32Error() : 0;
            var hresult = wait == WaitFailed ? Marshal.GetHRForLastWin32Error() : 0;
            return $"source=native-process-handle;signaled={(wait == WaitObject0).ToString().ToLowerInvariant()};wait_status={wait};hresult=0x{hresult:X8};native_error={nativeError}";
        }
        finally
        {
            if (nativeHandleReferenceAdded)
            {
                nativeProcessHandle.DangerousRelease();
            }
        }
    }

    private uint GetExitCode()
    {
        if (_nativeProcessHandle is null)
        {
            return _process.HasExited ? unchecked((uint)_process.ExitCode) : StillActive;
        }

        return GetExitCode(_nativeProcessHandle.DangerousGetHandle());
    }

    private static uint GetExitCode(IntPtr nativeProcessHandle)
    {
        if (!GetExitCodeProcess(nativeProcessHandle, out var exitCode))
        {
            throw LastWin32Exception("The cross-process child exit code could not be read.");
        }

        return exitCode;
    }

    internal static CrossProcessProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return OperatingSystem.IsWindows()
            ? StartWindows(startInfo)
            : StartManaged(startInfo);
    }

    internal void TerminateProcessTree()
    {
        if (_job is not null && !_job.IsInvalid)
        {
            if (!TerminateJobObject(_job.DangerousGetHandle(), 1))
            {
                var error = (uint)Marshal.GetLastWin32Error();
                throw new Win32Exception((int)error, "The cross-process child job could not be terminated.");
            }

            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        _standardInput?.Dispose();
        _job?.Dispose();
        _nativeProcessHandle?.Dispose();
        lock (_streamReadGate)
        {
            _streamDisposalRequested = true;
            if (_activeStreamReads == 0)
            {
                DisposeStreamsNoLock();
            }
        }
    }

    internal Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
        => ReadStreamToEndAsync(StandardOutput, cancellationToken);

    internal Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
        => ReadStreamToEndAsync(StandardError, cancellationToken);

    private static CrossProcessProcess StartManaged(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The cross-process child could not be started.");
        var ownership = CreateManagedOwnership(process, null, startInfo);
        return new CrossProcessProcess(process, ownership);
    }

    private static CrossProcessProcess StartWindows(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("Suspended cross-process children require UseShellExecute=false.");
        }

        var job = CreateConfiguredJob();
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeFileHandle? errorRead = null;
        SafeFileHandle? errorWrite = null;
        Process? process = null;
        SafeFileHandle? nativeProcessHandle = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;
        StreamWriter? standardInput = null;
        var processInformation = default(ProcessInformationData);
        var processCreated = false;
        var processResumed = false;
        var processHandleTransferred = false;

        try
        {
            if (startInfo.RedirectStandardInput)
            {
                (inputRead, inputWrite) = CreatePipePair();
                MakeNonInheritable(inputWrite);
            }

            if (startInfo.RedirectStandardOutput)
            {
                (outputRead, outputWrite) = CreatePipePair();
                MakeNonInheritable(outputRead);
            }

            if (startInfo.RedirectStandardError)
            {
                (errorRead, errorWrite) = CreatePipePair();
                MakeNonInheritable(errorRead);
            }

            var startupInfo = new StartupInfoData
            {
                Size = (uint)Marshal.SizeOf<StartupInfoData>(),
                Flags = StartfUseStdHandles,
                StandardInput = inputRead?.DangerousGetHandle() ?? GetStdHandle(StandardInputHandle),
                StandardOutput = outputWrite?.DangerousGetHandle() ?? GetStdHandle(StandardOutputHandle),
                StandardError = errorWrite?.DangerousGetHandle() ?? GetStdHandle(StandardErrorHandle),
            };
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var environmentBlock = BuildEnvironmentBlock(startInfo);
            var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
            try
            {
                var creationFlags = CreateSuspended | CreateUnicodeEnvironment;
                if (startInfo.CreateNoWindow)
                {
                    creationFlags |= CreateNoWindow;
                }

                if (!CreateProcess(
                    applicationName: null,
                    commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environmentPointer,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInformation))
                {
                    throw LastWin32Exception("The suspended cross-process child could not be started.");
                }

                processCreated = true;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentPointer);
            }

            process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
            if (!AssignProcessToJobObject(job.DangerousGetHandle(), processInformation.ProcessHandle))
            {
                throw LastWin32Exception("The suspended cross-process child could not be assigned to its cleanup job.");
            }

            nativeProcessHandle = new SafeFileHandle(processInformation.ProcessHandle, ownsHandle: true);
            processHandleTransferred = true;

            if (startInfo.RedirectStandardOutput)
            {
                standardOutput = CreateReader(outputRead, startInfo.StandardOutputEncoding);
                outputRead = null;
            }

            if (startInfo.RedirectStandardError)
            {
                standardError = CreateReader(errorRead, startInfo.StandardErrorEncoding);
                errorRead = null;
            }

            if (startInfo.RedirectStandardInput)
            {
                standardInput = CreateWriter(inputWrite, startInfo.StandardInputEncoding);
                inputWrite = null;
            }

            if (ResumeThread(processInformation.ThreadHandle) == Infinite)
            {
                throw LastWin32Exception("The suspended cross-process child could not be resumed.");
            }

            processResumed = true;
            var ownership = new CrossProcessProcessOwnership(
                process,
                nativeProcessHandle,
                job,
                standardOutput,
                standardError,
                standardInput);
            job = null!;
            nativeProcessHandle = null;
            standardOutput = null;
            standardError = null;
            standardInput = null;
            return new CrossProcessProcess(process, ownership);
        }
        catch
        {
            if (processCreated && !processResumed)
            {
                TerminateProcess(processInformation.ProcessHandle, 1);
            }

            throw;
        }
        finally
        {
            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ThreadHandle);
            }

            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            errorRead?.Dispose();
            errorWrite?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            standardInput?.Dispose();
            job?.Dispose();
            nativeProcessHandle?.Dispose();
            if (!processHandleTransferred && processInformation.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ProcessHandle);
            }
        }
    }

    private static CrossProcessProcessOwnership CreateManagedOwnership(
        Process process,
        SafeFileHandle? job,
        ProcessStartInfo startInfo)
        => new(
            process,
            null,
            job,
            startInfo.RedirectStandardOutput ? process.StandardOutput : null,
            startInfo.RedirectStandardError ? process.StandardError : null,
            startInfo.RedirectStandardInput ? process.StandardInput : null);

    private async Task<string> ReadStreamToEndAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        lock (_streamReadGate)
        {
            if (_streamDisposalRequested)
            {
                throw new ObjectDisposedException(nameof(CrossProcessProcessOwnership));
            }

            _activeStreamReads++;
        }

        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
        finally
        {
            lock (_streamReadGate)
            {
                _activeStreamReads--;
                if (_streamDisposalRequested && _activeStreamReads == 0)
                {
                    DisposeStreamsNoLock();
                }
            }
        }
    }

    private void DisposeStreamsNoLock()
    {
        if (_streamsDisposed)
        {
            return;
        }

        _standardOutput?.Dispose();
        _standardError?.Dispose();
        _streamsDisposed = true;
    }

    private static SafeFileHandle CreateConfiguredJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw LastWin32Exception("The cross-process child job could not be created.");
        }

        var job = new SafeFileHandle(handle, ownsHandle: true);
        try
        {
            var limits = new JobObjectExtendedLimitInformationData
            {
                BasicLimitInformation = new JobObjectBasicLimitInformationData
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(job.DangerousGetHandle(), JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    throw LastWin32Exception("The cross-process child job limits could not be configured.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipePair()
    {
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0))
        {
            throw LastWin32Exception("The cross-process child pipes could not be created.");
        }

        try
        {
            if (!SetHandleInformation(read, HandleFlagInherit, HandleFlagInherit)
                || !SetHandleInformation(write, HandleFlagInherit, HandleFlagInherit))
            {
                throw LastWin32Exception("The cross-process child pipe could not be made inheritable.");
            }

            return (read, write);
        }
        catch
        {
            read.Dispose();
            write.Dispose();
            throw;
        }
    }

    private static void MakeNonInheritable(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
        {
            throw LastWin32Exception("The cross-process child pipe could not be made private.");
        }
    }

    private static StreamReader CreateReader(SafeFileHandle? handle, Encoding? encoding)
    {
        if (handle is null)
        {
            throw new InvalidOperationException("The redirected cross-process output pipe was not created.");
        }

        var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        return new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    private static StreamWriter CreateWriter(SafeFileHandle? handle, Encoding? encoding)
    {
        if (handle is null)
        {
            throw new InvalidOperationException("The redirected cross-process input pipe was not created.");
        }

        var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        return new StreamWriter(stream, encoding ?? Encoding.UTF8, 4096) { AutoFlush = true };
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList
            : string.IsNullOrWhiteSpace(startInfo.Arguments)
                ? []
                : [startInfo.Arguments];
        return string.Join(' ', new[] { QuoteCommandLineArgument(startInfo.FileName) }.Concat(arguments.Select(QuoteCommandLineArgument)));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var entries = startInfo.Environment
            .Select(pair => pair.Value is null ? pair.Key : pair.Key + "=" + pair.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return string.Join('\0', entries) + "\0\0";
    }

    private static Win32Exception LastWin32Exception(string message)
        => new(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, uint jobObjectInformationClass, IntPtr jobObjectInformation, uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoData startupInfo,
        out ProcessInformationData processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint standardHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoData
    {
        internal uint Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationData
    {
        internal IntPtr ProcessHandle;
        internal IntPtr ThreadHandle;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        internal JobObjectBasicLimitInformationData BasicLimitInformation;
        internal JobObjectIoCountersData IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationData
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectIoCountersData
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }
}
