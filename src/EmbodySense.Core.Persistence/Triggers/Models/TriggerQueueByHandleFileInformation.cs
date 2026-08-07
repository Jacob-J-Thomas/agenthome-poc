using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Maps the native Windows file identity fields required by trigger queue path validation.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TriggerQueueByHandleFileInformation
{
    internal uint FileAttributes;
    internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    internal uint VolumeSerialNumber;
    internal uint FileSizeHigh;
    internal uint FileSizeLow;
    internal uint NumberOfLinks;
    internal uint FileIndexHigh;
    internal uint FileIndexLow;
}
