using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct CustomLoopRunLinuxStatx
{
    [FieldOffset(0)]
    public uint Mask;

    [FieldOffset(16)]
    public uint LinkCount;

    [FieldOffset(28)]
    public ushort Mode;

    [FieldOffset(32)]
    public ulong Inode;

    [FieldOffset(136)]
    public uint DeviceMajor;

    [FieldOffset(140)]
    public uint DeviceMinor;
}
