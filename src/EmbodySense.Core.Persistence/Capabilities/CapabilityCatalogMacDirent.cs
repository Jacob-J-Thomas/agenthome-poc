using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogMacDirent
{
    internal ulong Inode;
    internal ulong SeekOffset;
    internal ushort RecordLength;
    internal ushort NameLength;
    internal byte Type;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1_024)]
    internal byte[] Name;
}
