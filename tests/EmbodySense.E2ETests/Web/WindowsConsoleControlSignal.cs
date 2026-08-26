using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EmbodySense.E2ETests.Web;

/// <summary>Sends a Ctrl+Break control event to an isolated Windows console process group.</summary>
/// <remarks>
/// The receiver must have been created with its own console and process group. The caller temporarily attaches to
/// that console solely to generate the event; cleanup force-kill remains outside this graceful signal path.
/// </remarks>
internal static class WindowsConsoleControlSignal
{
    private const uint CtrlBreakEvent = 1;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>Sends Ctrl+Break to the process group whose identifier equals <paramref name="processId"/>.</summary>
    public static void SendCtrlBreak(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows console control is only available on Windows.");
        }

        var restoreParentConsole = FreeConsole();
        var attached = false;
        try
        {
            if (!AttachConsole(unchecked((uint)processId)))
            {
                throw LastWin32Exception("The external Web process console could not be attached for graceful shutdown.");
            }

            attached = true;
            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, unchecked((uint)processId)))
            {
                throw LastWin32Exception("The external Web process Ctrl+Break shutdown signal could not be generated.");
            }
        }
        finally
        {
            if (attached)
            {
                FreeConsole();
            }
            if (restoreParentConsole)
            {
                AttachConsole(AttachParentProcess);
            }
        }
    }

    private static Win32Exception LastWin32Exception(string message)
        => new(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlType, uint processGroupId);
}
