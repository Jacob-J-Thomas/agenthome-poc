using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;

namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class WindowsCredentialStore : IWindowsCredentialStore
{
    private const int GenericCredentialType = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumNativeBlobBytes = 2_560;

    public bool IsSupported => OperatingSystem.IsWindows();
    public int MaxValueByteLength => MaximumNativeBlobBytes;

    public WindowsCredentialStoreStatus Probe(string target)
    {
        if (!IsSupported)
        {
            return WindowsCredentialStoreStatus.Unavailable;
        }

        if (!CredRead(target, GenericCredentialType, 0, out var credentialPointer))
        {
            return Marshal.GetLastPInvokeError() == ErrorNotFound ? WindowsCredentialStoreStatus.Missing : WindowsCredentialStoreStatus.Unavailable;
        }

        try
        {
            if (credentialPointer == IntPtr.Zero)
            {
                return WindowsCredentialStoreStatus.Corrupt;
            }

            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return credential.CredentialBlobSize is > 0 and <= MaximumNativeBlobBytes && credential.CredentialBlob != IntPtr.Zero ? WindowsCredentialStoreStatus.Success : WindowsCredentialStoreStatus.Corrupt;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidOperationException)
        {
            return WindowsCredentialStoreStatus.Corrupt;
        }
        finally
        {
            ZeroAndFree(credentialPointer);
        }
    }

    public WindowsCredentialReadResult Read(string target)
    {
        if (!IsSupported)
        {
            return WindowsCredentialReadResult.Failed(WindowsCredentialStoreStatus.Unavailable);
        }

        if (!CredRead(target, GenericCredentialType, 0, out var credentialPointer))
        {
            return Marshal.GetLastPInvokeError() == ErrorNotFound ? WindowsCredentialReadResult.Missing() : WindowsCredentialReadResult.Failed(WindowsCredentialStoreStatus.Unavailable);
        }

        try
        {
            if (credentialPointer == IntPtr.Zero)
            {
                return WindowsCredentialReadResult.Failed(WindowsCredentialStoreStatus.Corrupt);
            }

            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize is 0 or > MaximumNativeBlobBytes || credential.CredentialBlob == IntPtr.Zero)
            {
                return WindowsCredentialReadResult.Failed(WindowsCredentialStoreStatus.Corrupt);
            }

            var value = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, value, 0, value.Length);
            return WindowsCredentialReadResult.Found(value);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidOperationException)
        {
            return WindowsCredentialReadResult.Failed(WindowsCredentialStoreStatus.Corrupt);
        }
        finally
        {
            ZeroAndFree(credentialPointer);
        }
    }

    public WindowsCredentialStoreStatus Write(string target, byte[] value)
    {
        if (!IsSupported)
        {
            return WindowsCredentialStoreStatus.Unavailable;
        }

        if (value.Length is 0 or > MaximumNativeBlobBytes)
        {
            return WindowsCredentialStoreStatus.LimitExceeded;
        }

        var pinnedValue = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = target,
                CredentialBlobSize = value.Length,
                CredentialBlob = pinnedValue.AddrOfPinnedObject(),
                Persist = PersistLocalMachine,
                UserName = "EmbodySense"
            };
            return CredWrite(ref credential, 0) ? WindowsCredentialStoreStatus.Success : WindowsCredentialStoreStatus.Unavailable;
        }
        finally
        {
            pinnedValue.Free();
        }
    }

    public WindowsCredentialStoreStatus Delete(string target)
    {
        if (!IsSupported)
        {
            return WindowsCredentialStoreStatus.Unavailable;
        }

        if (CredDelete(target, GenericCredentialType, 0))
        {
            return WindowsCredentialStoreStatus.Success;
        }

        return Marshal.GetLastPInvokeError() == ErrorNotFound ? WindowsCredentialStoreStatus.Missing : WindowsCredentialStoreStatus.Unavailable;
    }

    private static void ZeroAndFree(IntPtr credentialPointer)
    {
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize is > 0 and <= MaximumNativeBlobBytes)
            {
                var zeros = new byte[credential.CredentialBlobSize];
                Marshal.Copy(zeros, 0, credential.CredentialBlob, zeros.Length);
                CryptographicOperations.ZeroMemory(zeros);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidOperationException)
        {
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
}
