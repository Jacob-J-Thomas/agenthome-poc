using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.E2ETests.Web;

/// <summary>Starts a Windows-only external test process in its own console and process group.</summary>
/// <remarks>
/// The isolated console makes a Ctrl+Break event target only the external host process group. This is test
/// infrastructure, not an application shutdown endpoint; callers must use <see cref="StopAsync"/> before
/// cleanup disposal. The type is never instantiated outside Windows.
/// </remarks>
internal sealed class WindowsConsoleControlledProcess : IAsyncDisposable
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StandardInputHandle = unchecked((uint)-10);
    private const uint StandardOutputHandle = unchecked((uint)-11);
    private const uint StandardErrorHandle = unchecked((uint)-12);

    private readonly StreamReader _standardOutput;
    private readonly StreamReader _standardError;
    private readonly Task _standardOutputPump;
    private readonly Task _standardErrorPump;

    private WindowsConsoleControlledProcess(
        Process process,
        StreamReader standardOutput,
        StreamReader standardError,
        ProcessOutputBuffer output,
        ProcessOutputBuffer error)
    {
        Process = process;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _standardOutputPump = PumpAsync(standardOutput, output);
        _standardErrorPump = PumpAsync(standardError, error);
    }

    /// <summary>Gets the externally hosted Web process.</summary>
    public Process Process { get; }

    /// <summary>Sends Ctrl+Break to the isolated process group and waits for generic-host shutdown.</summary>
    /// <exception cref="TimeoutException">Thrown when the host does not stop within the bounded graceful window.</exception>
    public async Task StopAsync()
    {
        WindowsConsoleControlSignal.SendCtrlBreak(Process.Id);
        await WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Performs cleanup-only force termination if a prior graceful shutdown did not complete.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }

        await WaitForExitAsync();
        _standardOutput.Dispose();
        _standardError.Dispose();
    }

    /// <summary>Starts the supplied test host with redirected output and an isolated Windows console.</summary>
    /// <remarks>
    /// <see cref="ProcessStartInfo.CreateNoWindow"/> is intentionally not applied on this path because a genuine
    /// console is required for the graceful control event. Unix keeps the supplied no-window setting and uses SIGINT.
    /// </remarks>
    public static WindowsConsoleControlledProcess Start(ProcessStartInfo startInfo, ProcessOutputBuffer output, ProcessOutputBuffer error)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows console control is only available on Windows.");
        }
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException("Windows console-controlled processes require shell-free redirected standard output and error.");
        }

        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeFileHandle? errorRead = null;
        SafeFileHandle? errorWrite = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;
        var processInformation = default(ProcessInformationData);
        var processCreated = false;
        try
        {
            (outputRead, outputWrite) = CreatePipePair();
            MakeNonInheritable(outputRead);
            (errorRead, errorWrite) = CreatePipePair();
            MakeNonInheritable(errorRead);
            var startupInfo = new StartupInfoData
            {
                Size = (uint)Marshal.SizeOf<StartupInfoData>(),
                Flags = StartfUseStdHandles,
                StandardInput = GetStdHandle(StandardInputHandle),
                StandardOutput = outputWrite.DangerousGetHandle(),
                StandardError = errorWrite.DangerousGetHandle(),
            };
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var environmentPointer = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo));
            try
            {
                if (!CreateProcess(
                    applicationName: null,
                    commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: true,
                    CreateNewConsole | CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environmentPointer,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInformation))
                {
                    throw LastWin32Exception("The isolated external Web process could not be started.");
                }

                processCreated = true;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentPointer);
            }

            var process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
            standardOutput = CreateReader(outputRead, startInfo.StandardOutputEncoding);
            outputRead = null;
            standardError = CreateReader(errorRead, startInfo.StandardErrorEncoding);
            errorRead = null;
            var result = new WindowsConsoleControlledProcess(
                process,
                standardOutput,
                standardError,
                output,
                error);
            standardOutput = null;
            standardError = null;
            return result;
        }
        catch
        {
            if (processCreated && processInformation.ProcessHandle != IntPtr.Zero)
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
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ProcessHandle);
            }

            outputRead?.Dispose();
            outputWrite?.Dispose();
            errorRead?.Dispose();
            errorWrite?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
        }
    }

    private async Task WaitForExitAsync()
    {
        await Process.WaitForExitAsync();
        await Task.WhenAll(_standardOutputPump, _standardErrorPump);
    }

    private static async Task PumpAsync(StreamReader reader, ProcessOutputBuffer output)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            output.Append(line);
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipePair()
    {
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0))
        {
            throw LastWin32Exception("The external Web process output pipe could not be created.");
        }

        try
        {
            if (!SetHandleInformation(read, HandleFlagInherit, HandleFlagInherit)
                || !SetHandleInformation(write, HandleFlagInherit, HandleFlagInherit))
            {
                throw LastWin32Exception("The external Web process output pipe could not be made inheritable.");
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
            throw LastWin32Exception("The external Web process parent pipe could not be made private.");
        }
    }

    private static StreamReader CreateReader(SafeFileHandle handle, Encoding? encoding)
        => new(new FileStream(handle, FileAccess.Read, 4096, isAsync: false), encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

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
        => string.Join('\0', startInfo.Environment.Select(pair => pair.Value is null ? pair.Key : pair.Key + "=" + pair.Value).Order(StringComparer.Ordinal)) + "\0\0";

    private static Win32Exception LastWin32Exception(string message)
        => new(Marshal.GetLastWin32Error(), message);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

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
}
