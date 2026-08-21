using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct LinuxStatxBuffer
{
    [FieldOffset(0)]
    public uint Mask;

    [FieldOffset(16)]
    public uint LinkCount;

    [FieldOffset(20)]
    public uint OwnerId;

    [FieldOffset(24)]
    public uint GroupId;

    [FieldOffset(28)]
    public ushort Mode;

    [FieldOffset(32)]
    public ulong Inode;

    [FieldOffset(80)]
    public long BirthTimeSeconds;

    [FieldOffset(88)]
    public uint BirthTimeNanoseconds;

    [FieldOffset(136)]
    public uint DeviceMajor;

    [FieldOffset(140)]
    public uint DeviceMinor;

    [FieldOffset(144)]
    public ulong MountId;
}
