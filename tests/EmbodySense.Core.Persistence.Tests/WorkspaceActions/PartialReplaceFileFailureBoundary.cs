using System.ComponentModel;
using System.Runtime.InteropServices;
using EmbodySense.Core.Persistence.WorkspaceActions;

namespace EmbodySense.Core.Persistence.Tests.WorkspaceActions;

/// <summary>Models the documented partial ReplaceFileW namespace shapes for Windows-only recovery tests.</summary>
internal sealed class PartialReplaceFileFailureBoundary : IWorkspaceActionWindowsReplacementBoundary
{
    public const int UnableToRemoveReplaced = 1175;
    public const int UnableToMoveReplacement = 1176;
    public const int UnableToMoveReplacement2 = 1177;

    public PartialReplaceFileFailureBoundary(int nativeErrorCode)
    {
        if (nativeErrorCode is not (UnableToRemoveReplaced or UnableToMoveReplacement or UnableToMoveReplacement2))
        {
            throw new ArgumentOutOfRangeException(nameof(nativeErrorCode));
        }
        NativeErrorCode = nativeErrorCode;
    }

    public int NativeErrorCode { get; }

    public int InvocationCount { get; private set; }

    public void Replace(string replacedPath, string replacementPath, string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The partial ReplaceFileW boundary is Windows-only.");
        }
        InvocationCount++;
        if (NativeErrorCode == UnableToMoveReplacement2
            && !MoveFileEx(replacedPath, backupPath, 0))
        {
            throw new IOException(
                "The partial ReplaceFileW test shape could not move the replaced target to its reserved backup name.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        throw new IOException(
            $"ReplaceFileW returned documented partial failure {NativeErrorCode}.",
            new Win32Exception(NativeErrorCode));
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
