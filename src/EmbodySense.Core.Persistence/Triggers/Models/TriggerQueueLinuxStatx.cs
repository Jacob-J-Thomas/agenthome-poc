using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Maps the architecture-independent Linux kernel <c>statx</c> ABI fields required by trigger queue path validation.</summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct TriggerQueueLinuxStatx
{
    [FieldOffset(0)]
    internal uint Mask;

    [FieldOffset(16)]
    internal uint LinkCount;

    [FieldOffset(28)]
    internal ushort Mode;

    [FieldOffset(32)]
    internal ulong Inode;

    [FieldOffset(136)]
    internal uint DeviceMajor;

    [FieldOffset(140)]
    internal uint DeviceMinor;
}
