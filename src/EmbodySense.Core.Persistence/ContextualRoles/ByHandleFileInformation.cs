using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public NativeFileTime CreationTime;
    public NativeFileTime LastAccessTime;
    public NativeFileTime LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
